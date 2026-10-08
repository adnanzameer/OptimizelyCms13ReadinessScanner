using NuGet.Versioning;

namespace OptimizelyCms13ReadinessScanner;

/// <summary>Supplies the managed assemblies of a NuGet package (best target framework only).</summary>
public interface IPackageContentSource
{
    /// <summary>Raw assembly images, or null if the package could not be downloaded.</summary>
    Task<IReadOnlyList<byte[]>?> GetAssembliesAsync(string id, NuGetVersion version, CancellationToken ct);

    /// <summary>Total bytes of package downloads so far (for the run summary); 0 if not tracked.</summary>
    long DownloadedBytes => 0;
}

/// <summary>
/// Builds, on demand and incrementally, the CMS 13 API surface a project should be compared with.
/// For each first-party package the project restored (EPiServer.* / Optimizely.*, direct or
/// transitive) it picks the version that belongs to the target CMS line, downloads it and indexes
/// its assemblies:
///   - CMS platform packages: the lowest stable release of the target major (13.0.x), the most
///     conservative baseline - an API missing there is missing from CMS 13.
///   - other first-party packages: the first release the compatibility check says supports the target.
/// Packages with no CMS 13 build are listed in <see cref="Summary"/>, never silently ignored.
/// </summary>
public sealed class ApiSurfaceProvider
{
    private readonly IPackageContentSource _content;
    private readonly IPackageMetadataSource _metadata;
    private readonly PackageCompatibilityChecker _checker;
    private readonly Action<string> _warn;
    private readonly ApiSurface _surface = new();
    private readonly HashSet<string> _indexed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _noTargetBuild = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _elapsed;

    public ApiSurfaceProvider(IPackageContentSource content, IPackageMetadataSource metadata,
        PackageCompatibilityChecker checker, int targetMajor, Action<string> warn)
    {
        _content = content;
        _metadata = metadata;
        _checker = checker;
        TargetMajor = targetMajor;
        _warn = warn;
    }

    public int TargetMajor { get; }

    /// <summary>One line describing what the API check could and could not cover.</summary>
    public string Summary
    {
        get
        {
            var text = $"CMS {TargetMajor} API check indexed {_surface.AssemblyNames.Count} assemblies ({_surface.TypeCount} types) from {_indexed.Count} package(s), {_content.DownloadedBytes / (1024 * 1024)} MB downloaded in {_elapsed.TotalSeconds:0}s.";
            if (_noTargetBuild.Count > 0) text += $" No CMS {TargetMajor} build found for: {string.Join(", ", _noTargetBuild)}.";
            if (_failed.Count > 0) text += $" Download failed for: {string.Join(", ", _failed)}.";
            return text;
        }
    }

    public ApiSurface? EnsureFor(ProjectContext project)
    {
        if (project.AllPackageVersions == null || project.AllPackageVersions.Count == 0)
        {
            _warn($"{Path.GetFileName(project.ProjectFilePath)}: --check-api needs a restored project (obj/project.assets.json); skipped.");
            return null;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { EnsureAsync(project.AllPackageVersions, cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { _warn("CMS API check timed out while downloading packages; results may be incomplete."); }
        _elapsed += clock.Elapsed;
        return _surface;
    }

    // CMS 13 reorganised the platform: packages such as EPiServer.CMS.Core are now thin and pull the
    // real assemblies (EPiServer, EPiServer.Cache, EPiServer.Blobs, ...) in as pinned dependencies
    // that a CMS 12 restore never lists. So the index follows the first-party dependencies of every
    // package it downloads, not just the packages restored today. Depth is bounded as a safety net.
    private const int MaxDependencyDepth = 6;

    // Feeds are the bottleneck (a typical solution pulls ~100 packages), so each level of the
    // dependency walk is fetched in parallel. Only the network calls run concurrently: assemblies are
    // added to the shared index one at a time, in a deterministic order, on this thread.
    private const int MaxParallelRequests = 8;

    private async Task EnsureAsync(IReadOnlyDictionary<string, string> restored, CancellationToken ct)
    {
        var firstParty = restored.Where(kv => IsFirstParty(kv.Key)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var picks = await SelectBoundedAsync(firstParty, async kv => (kv.Key, Version: await PickVersionAsync(kv.Key, kv.Value, ct)));

        var frontier = new List<(string Id, NuGetVersion Version)>();
        foreach (var (id, version) in picks)
        {
            if (version == null) _noTargetBuild.Add(id);
            else frontier.Add((id, version));
        }

        for (var depth = 0; frontier.Count > 0; depth++)
        {
            var batch = frontier
                .GroupBy(p => p.Id + "/" + p.Version.ToNormalizedString(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Where(p => !_indexed.Contains(p.Id + "/" + p.Version.ToNormalizedString()) && !_failed.Contains(p.Id))
                .ToList();
            if (batch.Count == 0) break;

            var downloads = await SelectBoundedAsync(batch, async p => (Package: p, Assemblies: await _content.GetAssembliesAsync(p.Id, p.Version, ct)));

            var indexedNow = new List<(string Id, NuGetVersion Version)>();
            foreach (var (package, assemblies) in downloads)
            {
                if (assemblies == null) { _failed.Add(package.Id); continue; }
                foreach (var image in assemblies) _surface.AddAssembly(image);
                _indexed.Add(package.Id + "/" + package.Version.ToNormalizedString());
                indexedNow.Add(package);
            }
            if (depth >= MaxDependencyDepth) break;

            var dependencyLists = await SelectBoundedAsync(indexedNow, async p => await _metadata.GetDependenciesAsync(p.Id, p.Version, ct));
            var dependencies = dependencyLists
                .Where(list => list != null)
                .SelectMany(list => list!)
                .Where(d => IsFirstParty(d.Id))
                .GroupBy(d => d.Id + " " + d.VersionRange, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var resolved = await SelectBoundedAsync(dependencies, async d => (d.Id, Version: await DependencyVersionAsync(d, ct)));
            frontier = resolved.Where(r => r.Version != null).Select(r => (r.Id, r.Version!)).ToList();
        }
    }

    // Runs the async selector over every item with bounded concurrency, keeping the input order.
    private static async Task<List<TOut>> SelectBoundedAsync<TIn, TOut>(IEnumerable<TIn> items, Func<TIn, Task<TOut>> selector)
    {
        using var gate = new SemaphoreSlim(MaxParallelRequests);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync();
            try { return await selector(item); }
            finally { gate.Release(); }
        }).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }

    // The version of a dependency to index: its range's minimum when that is already on the target
    // line, otherwise the target line's own release if the package has one (a loose "[12.0, )" range
    // must not pull the CMS 12 build in, which would hide exactly the removals being looked for).
    private async Task<NuGetVersion?> DependencyVersionAsync(NuGet.Packaging.Core.PackageDependency dependency, CancellationToken ct)
    {
        var min = dependency.VersionRange.MinVersion;
        if (min != null && min.Major == TargetMajor) return min;
        return await LowestStableOfTargetMajorAsync(dependency.Id, ct) ?? min;
    }

    private static bool IsFirstParty(string id) =>
        (id.Equals("EPiServer", StringComparison.OrdinalIgnoreCase) || id.StartsWith("EPiServer.", StringComparison.OrdinalIgnoreCase) ||
         id.StartsWith("Optimizely.", StringComparison.OrdinalIgnoreCase)) &&
        !id.StartsWith("EPiServer.Find", StringComparison.OrdinalIgnoreCase);

    private async Task<NuGetVersion?> PickVersionAsync(string id, string currentVersion, CancellationToken ct)
    {
        if (PackageCompatibilityChecker.IsCmsPackage(id))
            return await LowestStableOfTargetMajorAsync(id, ct);

        var result = await _checker.CheckAsync(id, currentVersion, ct);
        switch (result.Status)
        {
            case PackageCompatStatus.UpgradeAvailable or PackageCompatStatus.CompatibleNewerNet10Available
                when NuGetVersion.TryParse(result.FirstCompatibleVersion, out var first):
                return first;
            case PackageCompatStatus.Compatible:
                // The installed build already supports the target, so its assemblies ARE the CMS 13
                // surface for this package; index them so its symbols are not mistaken for missing.
                return NuGetVersion.TryParse(currentVersion, out var installed) ? installed : null;
            case PackageCompatStatus.NoCompatibleRelease:
                return null;
            default: // NotCmsDependent (e.g. EPiServer.Framework) / Unknown: follow the CMS version line if it has one
                return await LowestStableOfTargetMajorAsync(id, ct);
        }
    }

    private async Task<NuGetVersion?> LowestStableOfTargetMajorAsync(string id, CancellationToken ct)
    {
        var versions = await _metadata.GetVersionsAsync(id, ct);
        return versions?.Where(v => !v.IsPrerelease && v.Major == TargetMajor).OrderBy(v => v).FirstOrDefault();
    }
}
