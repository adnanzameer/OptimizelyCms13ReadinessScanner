using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace OptimizelyCms13ReadinessScanner;

public enum PackageCompatStatus
{
    /// <summary>The referenced version already allows the target CMS major version (by declared range).</summary>
    Compatible,
    /// <summary>Declared compatible, but a newer release targets only .NET 10+ (likely the build made for the new platform).</summary>
    CompatibleNewerNet10Available,
    /// <summary>The referenced version does not, but a newer release does.</summary>
    UpgradeAvailable,
    /// <summary>No published release found that allows the target CMS major version.</summary>
    NoCompatibleRelease,
    /// <summary>The package does not depend on the CMS, so CMS compatibility does not apply.</summary>
    NotCmsDependent,
    /// <summary>Could not be determined. See PackageCompatResult.Reason for which of several distinct causes applies.</summary>
    Unknown
}

public record PackageCompatResult(
    string PackageId,
    string? CurrentVersion,
    PackageCompatStatus Status,
    string? FirstCompatibleVersion = null,
    string? LatestVersion = null,
    // Populated for Status == Unknown: a specific, human-readable explanation distinguishing "this
    // package was not found on any configured feed" from "the referenced version was not found
    // (but the package is known)" from "a feed could not be queried" from "the check did not
    // complete (timed out / faulted)" - these need different actions from the reader and were
    // previously all reported with the same generic message regardless of cause.
    string? Reason = null);

/// <summary>Package metadata lookups the compatibility check needs; faked in tests.</summary>
public interface IPackageMetadataSource
{
    /// <summary>All published versions of a package, or null if it cannot be found / reached.</summary>
    Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct);

    /// <summary>Declared dependencies (all target-framework groups flattened) of one version, or null if unavailable.</summary>
    Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion version, CancellationToken ct);

    /// <summary>
    /// True when every target-framework group of this version is .NET (Core) {minMajor} or later, i.e.
    /// the version can only be consumed by a project already on the new platform. Optional: the
    /// default says "unknown".
    /// </summary>
    Task<bool> TargetsOnlyDotNetAsync(string id, NuGetVersion version, int minMajor, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// Decides, from NuGet feed metadata alone, whether a package supports a target CMS major
/// version: a version "supports" it when every CMS dependency it declares (EPiServer.CMS*,
/// Optimizely.CMS*) has a version range that admits {targetMajor}.0.0. EPiServer.Framework is
/// deliberately not used as an anchor, because its version numbers do not track the CMS major.
/// </summary>
public sealed class PackageCompatibilityChecker : IDisposable
{
    // Newer releases examined per package; bounds feed traffic for packages with long histories.
    private const int MaxVersionsExamined = 12;

    // Bounds how many packages are checked concurrently against the feed at once. Without this,
    // a project with dozens of PackageReferences fires that many simultaneous HTTP requests (each
    // potentially fanning out further via the transitive first-party lookup in SupportsAsync),
    // which risks feed rate-limiting (observed as 429s, which would otherwise be silently
    // swallowed and reported as misleading "Unknown" results).
    private const int MaxConcurrentChecks = 8;

    private readonly IPackageMetadataSource _source;
    private readonly int _targetDotNetMajor;
    private readonly Dictionary<(string, string?), Task<PackageCompatResult>> _cache = new();
    private readonly SemaphoreSlim _throttle = new(MaxConcurrentChecks);

    public PackageCompatibilityChecker(IPackageMetadataSource source, int targetCmsMajor = 13, int targetDotNetMajor = 10)
    {
        _source = source;
        TargetCmsMajor = targetCmsMajor;
        _targetDotNetMajor = targetDotNetMajor;
    }

    public int TargetCmsMajor { get; }

    public static bool IsCmsPackage(string id) =>
        id.Equals("EPiServer.CMS", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("EPiServer.CMS.", StringComparison.OrdinalIgnoreCase) ||
        id.Equals("Optimizely.CMS", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("Optimizely.CMS.", StringComparison.OrdinalIgnoreCase);

    // Packages that are the platform itself (they ARE the upgrade) or never CMS add-ons.
    private static bool IsSkipped(string id) =>
        IsCmsPackage(id) ||
        id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("System.", StringComparison.OrdinalIgnoreCase);

    public Task<PackageCompatResult> CheckAsync(string id, string? currentVersion, CancellationToken ct)
    {
        lock (_cache)
        {
            var key = (id.ToLowerInvariant(), currentVersion);
            // A cached task that already finished unsuccessfully (faulted, or cancelled by a
            // DIFFERENT caller's timeout) must not be reused: a transient failure or a timeout
            // scoped to one project's CheckPackages() call would otherwise permanently poison the
            // result for every other project in the same solution scan that references the same
            // package. A task still in flight (not yet completed either way) IS reused, so
            // concurrent callers for the same package continue to share one in-flight lookup.
            if (_cache.TryGetValue(key, out var existing) && !IsUnsuccessfullyCompleted(existing))
                return existing;

            var task = CheckCoreAsync(id, currentVersion, ct);
            _cache[key] = task;
            return task;
        }
    }

    private static bool IsUnsuccessfullyCompleted(Task<PackageCompatResult> task) =>
        task.IsFaulted || task.IsCanceled;

    private async Task<PackageCompatResult> CheckCoreAsync(string id, string? currentVersion, CancellationToken ct)
    {
        if (IsSkipped(id))
            return new PackageCompatResult(id, currentVersion, PackageCompatStatus.NotCmsDependent);

        await _throttle.WaitAsync(ct);
        try
        {
            return await CheckThrottledAsync(id, currentVersion, ct);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private async Task<PackageCompatResult> CheckThrottledAsync(string id, string? currentVersion, CancellationToken ct)
    {
        var versions = await _source.GetVersionsAsync(id, ct);
        if (versions == null || versions.Count == 0)
            return new PackageCompatResult(id, currentVersion, PackageCompatStatus.Unknown,
                Reason: $"'{id}' was not found on any configured NuGet feed.");

        var stable = versions.Where(v => !v.IsPrerelease).OrderByDescending(v => v).ToList();
        var ordered = stable.Count > 0 ? stable : versions.OrderByDescending(v => v).ToList();
        var latest = ordered[0];

        NuGetVersion.TryParse(currentVersion, out var current);

        if (current != null)
        {
            var deps = await _source.GetDependenciesAsync(id, current, ct);
            if (deps == null)
            {
                // The package itself is known (we have its version list); distinguish "the exact
                // referenced version isn't published anywhere" - typically restore drift, a stale
                // Version attribute, or a private build - from "a feed returned the version list
                // but refused/failed to return that version's dependency metadata", which points
                // at a feed problem rather than the project's package reference.
                var reason = versions.Contains(current)
                    ? $"A configured feed could not return dependency information for '{id}' {currentVersion} even though that version is published."
                    : $"'{id}' {currentVersion} was not found on any configured feed (package is known; latest published is {latest.ToNormalizedString()}). This can happen after a Version edit without restoring, or if the project restored against a different feed.";
                return new PackageCompatResult(id, currentVersion, PackageCompatStatus.Unknown,
                    LatestVersion: latest.ToNormalizedString(), Reason: reason);
            }

            switch (await SupportsAsync(id, current, deps, 0, ct))
            {
                case null:
                    return new PackageCompatResult(id, currentVersion, PackageCompatStatus.NotCmsDependent);
                case true:
                    return await CompatibleResultAsync(id, current, currentVersion, ordered, latest, ct);
            }
        }

        // The current version does not support the target. Look at newer releases, newest first,
        // and walk down while they still support it to find the lowest compatible version.
        var newer = ordered.Where(v => current == null || v > current).Take(MaxVersionsExamined).ToList();
        NuGetVersion? lowestSupporting = null;
        var inconclusive = false;
        foreach (var v in newer)
        {
            var deps = await _source.GetDependenciesAsync(id, v, ct);
            if (deps == null)
                return new PackageCompatResult(id, currentVersion, PackageCompatStatus.Unknown,
                    LatestVersion: latest.ToNormalizedString(),
                    Reason: $"A configured feed could not return dependency information for '{id}' {v.ToNormalizedString()} while searching for a compatible release.");

            var verdict = await SupportsAsync(id, v, deps, 0, ct);
            if (verdict == true) lowestSupporting = v;
            else if (verdict == null && lowestSupporting == null && v == newer[0]) { inconclusive = true; break; } // no CMS signal at all
            else if (lowestSupporting != null || v == newer[0]) break; // newest is capped, or the run ended
        }

        // The newest release declares no CMS dependency we can read: without an installed version
        // that means the package is simply not CMS-bound; with one, we cannot say either way.
        if (inconclusive)
            return new PackageCompatResult(id, currentVersion,
                current == null ? PackageCompatStatus.NotCmsDependent : PackageCompatStatus.Unknown,
                LatestVersion: latest.ToNormalizedString(),
                Reason: current == null ? null : $"The newest release of '{id}' declares no readable CMS dependency, so compatibility for the referenced version could not be inferred.");

        return lowestSupporting != null
            ? new PackageCompatResult(id, currentVersion, PackageCompatStatus.UpgradeAvailable,
                lowestSupporting.ToNormalizedString(), latest.ToNormalizedString())
            : new PackageCompatResult(id, currentVersion, PackageCompatStatus.NoCompatibleRelease,
                LatestVersion: latest.ToNormalizedString());
    }

    // Declared ranges are often open-ended (">= 12.0.0"), which says nothing about whether the
    // package actually works on the new platform. If a newer release targets only .NET 10+, that is
    // the build made for it, so surface it as a hint without calling the current version broken.
    private async Task<PackageCompatResult> CompatibleResultAsync(
        string id, NuGetVersion current, string? currentVersion, List<NuGetVersion> ordered, NuGetVersion latest, CancellationToken ct)
    {
        if (!await _source.TargetsOnlyDotNetAsync(id, current, _targetDotNetMajor, ct))
        {
            NuGetVersion? lowest = null;
            foreach (var v in ordered.Where(v => v > current).Take(MaxVersionsExamined))
            {
                if (await _source.TargetsOnlyDotNetAsync(id, v, _targetDotNetMajor, ct)) lowest = v;
                else break;
            }
            if (lowest != null)
                return new PackageCompatResult(id, currentVersion, PackageCompatStatus.CompatibleNewerNet10Available,
                    lowest.ToNormalizedString(), latest.ToNormalizedString());
        }
        return new PackageCompatResult(id, currentVersion, PackageCompatStatus.Compatible);
    }

    // True when the range admits at least one version whose major is the target, e.g. both
    // [13.0.0, 14.0.0) and [13.3.0, 14.0.0) do, while [12.0.0, 13.0.0) does not.
    private bool Admits(VersionRange range)
    {
        var next = new NuGetVersion(TargetCmsMajor + 1, 0, 0);
        var firstOfTarget = new NuGetVersion(TargetCmsMajor, 0, 0);

        var lowerOk = !range.HasLowerBound ||
                      range.MinVersion! < next;
        var upperOk = !range.HasUpperBound ||
                      range.MaxVersion! > firstOfTarget ||
                      // [12.33.5, 13.0.0] is a ceiling for the 12 line, not support for 13; only an exact pin
                      // inside the target line (min >= 13.0.0) counts.
                      (range.MaxVersion! == firstOfTarget && range.IsMaxInclusive && range.HasLowerBound && range.MinVersion! >= firstOfTarget);
        return lowerOk && upperOk;
    }

    private const int MaxTransitiveDepth = 3;

    /// <summary>
    /// true = supports target, false = capped below it, null = no CMS dependency found. A package
    /// that only depends on another first-party package (e.g. EPiServer.ImageLibrary.ImageSharp ->
    /// EPiServer.ImageLibrary -> EPiServer.CMS.Core) is judged by what that dependency requires.
    /// </summary>
    private async Task<bool?> SupportsAsync(string id, NuGetVersion version, IReadOnlyList<PackageDependency> dependencies, int depth, CancellationToken ct)
    {
        var cms = dependencies.Where(d => IsCmsPackage(d.Id)).ToList();
        if (cms.Count > 0) return cms.All(d => Admits(d.VersionRange));
        if (depth >= MaxTransitiveDepth) return await FrameworkVerdictAsync(id, version, ct);

        var verdicts = new List<bool>();
        foreach (var dep in dependencies.Where(d => IsFirstPartyLibrary(d.Id)))
        {
            if (dep.VersionRange.MinVersion is not { } min) continue;
            var inner = await _source.GetDependenciesAsync(dep.Id, min, ct);
            if (inner == null) continue;
            var verdict = await SupportsAsync(dep.Id, min, inner, depth + 1, ct);
            if (verdict.HasValue) verdicts.Add(verdict.Value);
        }
        return verdicts.Count == 0 ? await FrameworkVerdictAsync(id, version, ct) : verdicts.All(v => v);
    }

    // First-party packages with no CMS dependency (e.g. EPiServer.ImageLibrary 13.x) can still be
    // CMS-line specific: a version that only targets .NET 10+ can only be used with CMS 13.
    private async Task<bool?> FrameworkVerdictAsync(string id, NuGetVersion version, CancellationToken ct) =>
        IsFirstPartyLibrary(id) && await _source.TargetsOnlyDotNetAsync(id, version, _targetDotNetMajor, ct) ? true : null;

    private static bool IsFirstPartyLibrary(string id) =>
        (id.Equals("EPiServer", StringComparison.OrdinalIgnoreCase) || id.StartsWith("EPiServer.", StringComparison.OrdinalIgnoreCase) ||
         id.StartsWith("Optimizely.", StringComparison.OrdinalIgnoreCase)) &&
        !id.StartsWith("EPiServer.Framework", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _throttle.Dispose();
}

/// <summary>Reads package metadata from the https NuGet feeds configured for a directory (nuget.config chain).</summary>
public sealed class NuGetMetadataSource : IPackageMetadataSource, IPackageContentSource, IDisposable
{
    private const string NuGetOrgV3 = "https://api.nuget.org/v3/index.json";

    private readonly List<(string Name, FindPackageByIdResource Resource)> _resources;
    private readonly SourceCacheContext _cache = new();
    private readonly Action<string> _warn;
    private readonly HashSet<string> _warnedSources = new(StringComparer.OrdinalIgnoreCase);

    private NuGetMetadataSource(List<(string Name, FindPackageByIdResource Resource)> resources, Action<string> warn)
    {
        _resources = resources;
        _warn = warn;
    }

    public int SourceCount => _resources.Count;

    public static async Task<NuGetMetadataSource> CreateAsync(string configRoot, Action<string> warn, CancellationToken ct)
    {
        var settings = Settings.LoadDefaultSettings(configRoot);
        var sources = new PackageSourceProvider(settings).LoadPackageSources()
            .Where(s => s.IsEnabled && s.IsHttps) // never talk to plain-http feeds
            .ToList();
        if (sources.Count == 0) sources.Add(new PackageSource(NuGetOrgV3));

        var resources = new List<(string, FindPackageByIdResource)>();
        foreach (var source in sources)
        {
            try
            {
                var repo = Repository.Factory.GetCoreV3(source);
                var resource = await repo.GetResourceAsync<FindPackageByIdResource>(ct);
                if (resource != null) resources.Add((source.Name, resource));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                warn($"package source '{source.Name}' is unavailable ({ex.GetType().Name}); skipping it.");
            }
        }
        return new NuGetMetadataSource(resources, warn);
    }

    // Warns once per source per NuGetMetadataSource instance (i.e. once per scan run, not once per
    // package) - a feed that is genuinely down will otherwise produce one warning per package
    // checked, drowning out everything else on the console.
    private void WarnOnce(string sourceName, string operation, Exception ex)
    {
        lock (_warnedSources)
        {
            if (!_warnedSources.Add(sourceName)) return;
        }
        _warn($"NuGet source '{sourceName}' failed during {operation} ({ex.GetType().Name}: {ex.Message}); " +
              "further errors from this source are suppressed for this run, and its packages may be reported as Unknown.");
    }

    public async Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct)
    {
        var all = new HashSet<NuGetVersion>();
        foreach (var (name, resource) in _resources)
        {
            try
            {
                var versions = await resource.GetAllVersionsAsync(id, _cache, NullLogger.Instance, ct);
                if (versions != null) all.UnionWith(versions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                WarnOnce(name, "a package version lookup", ex);
            }
        }
        return all.Count == 0 ? null : all.ToList();
    }

    public async Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion version, CancellationToken ct)
    {
        foreach (var (name, resource) in _resources)
        {
            try
            {
                var info = await resource.GetDependencyInfoAsync(id, version, _cache, NullLogger.Instance, ct);
                if (info != null) return info.DependencyGroups.SelectMany(g => g.Packages).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                WarnOnce(name, "a dependency lookup", ex);
            }
        }
        return null;
    }

    public async Task<bool> TargetsOnlyDotNetAsync(string id, NuGetVersion version, int minMajor, CancellationToken ct)
    {
        foreach (var (name, resource) in _resources)
        {
            try
            {
                var info = await resource.GetDependencyInfoAsync(id, version, _cache, NullLogger.Instance, ct);
                if (info == null) continue;
                var groups = info.DependencyGroups.ToList();
                return groups.Count > 0 && groups.All(g =>
                    g.TargetFramework.Framework == ".NETCoreApp" && g.TargetFramework.Version.Major >= minMajor);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                WarnOnce(name, "a target framework lookup", ex);
            }
        }
        return false;
    }

    // ---- IPackageContentSource -----------------------------------------------------------------

    private const long MaxAssemblyBytes = 150L * 1024 * 1024;
    private const long MaxPackageBytes = 400L * 1024 * 1024;
    private long _downloadedBytes;
    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

    /// <summary>
    /// Downloads a package and returns the assemblies of its best lib/ target framework. Everything
    /// is read in memory from the zip - nothing is extracted to disk (no zip-slip) and no assembly
    /// is ever loaded or run; the bytes are only parsed as metadata.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>?> GetAssembliesAsync(string id, NuGetVersion version, CancellationToken ct)
    {
        foreach (var (_, resource) in _resources)
        {
            try
            {
                using var nupkg = new MemoryStream();
                if (!await resource.CopyNupkgToStreamAsync(id, version, nupkg, _cache, NullLogger.Instance, ct)) continue;
                Interlocked.Add(ref _downloadedBytes, nupkg.Length);
                if (nupkg.Length > MaxPackageBytes) return null;

                nupkg.Position = 0;
                using var zip = new System.IO.Compression.ZipArchive(nupkg, System.IO.Compression.ZipArchiveMode.Read);
                var best = zip.Entries
                    .Where(e => IsLibAssembly(e.FullName, out _))
                    .GroupBy(e => { IsLibAssembly(e.FullName, out var folder); return folder; })
                    .Select(g => (Folder: g.Key, Score: FrameworkScore(g.Key), Entries: g.ToList()))
                    .Where(g => g.Score > 0)
                    .OrderByDescending(g => g.Score)
                    .FirstOrDefault();
                if (best.Entries == null) return Array.Empty<byte[]>();

                var images = new List<byte[]>();
                foreach (var entry in best.Entries)
                {
                    if (entry.Length > MaxAssemblyBytes) continue;
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, ct);
                    if (buffer.Length <= MaxAssemblyBytes) images.Add(buffer.ToArray());
                }
                return images;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // try the next feed
            }
        }
        return null;
    }

    // lib/<tfm>/<name>.dll, excluding satellite resource assemblies.
    private static bool IsLibAssembly(string path, out string folder)
    {
        folder = "";
        var parts = path.Split('/');
        if (parts.Length != 3 || !parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase)) return false;
        if (!parts[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            parts[2].EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)) return false;
        folder = parts[1];
        return true;
    }

    // Prefer the newest .NET (Core) build, then .NET Standard; anything else is not consumable by CMS 13.
    private static int FrameworkScore(string folder)
    {
        var framework = NuGet.Frameworks.NuGetFramework.ParseFolder(folder);
        return framework.Framework switch
        {
            ".NETCoreApp" => 100_000 + framework.Version.Major * 100 + framework.Version.Minor,
            ".NETStandard" => 50_000 + framework.Version.Major * 100 + framework.Version.Minor,
            _ => 0
        };
    }

    public void Dispose() => _cache.Dispose();
}

public static class PackageCompatibilityFindings
{
    public const string RuleId = "OPT13-010";
    public const string Title = "Third-Party Package CMS 13 Compatibility";

    /// <summary>Null when the result needs no finding (compatible, or not CMS-dependent).</summary>
    public static Finding? ToFinding(PackageCompatResult r, ProjectContext project, int targetMajor)
    {
        var label = r.CurrentVersion == null ? r.PackageId : $"{r.PackageId} {r.CurrentVersion}";
        var (severity, message, fix) = r.Status switch
        {
            PackageCompatStatus.UpgradeAvailable => (Severity.Warning,
                $"'{label}' does not support CMS {targetMajor}, but version {r.FirstCompatibleVersion} does.",
                $"Upgrade {r.PackageId} to {r.FirstCompatibleVersion} or later as part of the CMS {targetMajor} upgrade."),
            PackageCompatStatus.NoCompatibleRelease => (Severity.Warning,
                $"'{label}' does not support CMS {targetMajor} and no published release does (latest: {r.LatestVersion}).",
                "Check with the package author for a CMS " + targetMajor + " roadmap, or plan a replacement."),
            PackageCompatStatus.CompatibleNewerNet10Available => (Severity.Info,
                $"'{label}' declares compatibility with CMS {targetMajor}, but version {r.FirstCompatibleVersion} is the first release that targets only .NET 10 or later, which is likely the build made for CMS {targetMajor}.",
                $"Consider {r.PackageId} {r.FirstCompatibleVersion} or later, and verify the current version works on CMS {targetMajor}."),
            PackageCompatStatus.Unknown => (Severity.Info,
                r.Reason ?? $"CMS {targetMajor} compatibility of '{label}' could not be determined (not found on the configured NuGet feeds, or a feed was unreachable).",
                "Verify this package manually."),
            _ => (Severity.Info, "", "")
        };
        if (r.Status is PackageCompatStatus.Compatible or PackageCompatStatus.NotCmsDependent) return null;

        return new Finding(RuleId, Title, severity, message, project.ProjectFilePath,
            LineOf(project.ProjectFileContent, r.PackageId), fix);
    }

    private static int LineOf(string content, string packageId)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains($"Include=\"{packageId}\"", StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 1;
    }
}
