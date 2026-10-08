using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace OptimizelyCms13ReadinessScanner.Tests;

public class PackageCompatibilityCheckerTests
{
    private sealed class FakeSource : IPackageMetadataSource
    {
        private readonly Dictionary<string, Dictionary<string, (string Id, string Range)[]>> _packages = new(StringComparer.OrdinalIgnoreCase);
        public int DependencyLookups;

        public FakeSource Add(string id, string version, params (string Id, string Range)[] deps)
        {
            if (!_packages.TryGetValue(id, out var versions)) _packages[id] = versions = new();
            versions[version] = deps;
            return this;
        }

        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(
                _packages.TryGetValue(id, out var v) ? v.Keys.Select(NuGetVersion.Parse).ToList() : null);

        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion version, CancellationToken ct)
        {
            DependencyLookups++;
            if (_packages.TryGetValue(id, out var v) && v.TryGetValue(version.ToNormalizedString(), out var deps))
                return Task.FromResult<IReadOnlyList<PackageDependency>?>(
                    deps.Select(d => new PackageDependency(d.Id, VersionRange.Parse(d.Range))).ToList());
            return Task.FromResult<IReadOnlyList<PackageDependency>?>(null);
        }
    }

    private static Task<PackageCompatResult> Check(FakeSource s, string id, string? version) =>
        new PackageCompatibilityChecker(s).CheckAsync(id, version, CancellationToken.None);

    [Fact]
    public async Task Version_capped_below_13_with_a_newer_compatible_release_is_UpgradeAvailable()
    {
        var s = new FakeSource()
            .Add("Geta.X", "1.0.0", ("EPiServer.CMS.Core", "[12.0.0, 13.0.0)"))
            .Add("Geta.X", "2.0.0", ("EPiServer.CMS.Core", "[13.0.0, 14.0.0)"))
            .Add("Geta.X", "2.1.0", ("EPiServer.CMS.Core", "[13.0.0, 14.0.0)"));

        var r = await Check(s, "Geta.X", "1.0.0");

        Assert.Equal(PackageCompatStatus.UpgradeAvailable, r.Status);
        Assert.Equal("2.0.0", r.FirstCompatibleVersion); // lowest of the consecutive compatible run
        Assert.Equal("2.1.0", r.LatestVersion);
    }

    [Fact]
    public async Task All_releases_capped_below_13_is_NoCompatibleRelease()
    {
        var s = new FakeSource()
            .Add("Old.Addon", "1.0.0", ("EPiServer.CMS.UI", "[12.0.0, 13.0.0)"))
            .Add("Old.Addon", "1.1.0", ("EPiServer.CMS.UI", "[12.0.0, 13.0.0)"));

        var r = await Check(s, "Old.Addon", "1.0.0");

        Assert.Equal(PackageCompatStatus.NoCompatibleRelease, r.Status);
        Assert.Equal("1.1.0", r.LatestVersion);
    }

    [Fact]
    public async Task Open_ended_range_is_Compatible_without_looking_at_other_versions()
    {
        var s = new FakeSource().Add("Open.Addon", "3.0.0", ("EPiServer.CMS.Core", "12.0.0"));

        var r = await Check(s, "Open.Addon", "3.0.0");

        Assert.Equal(PackageCompatStatus.Compatible, r.Status);
        Assert.Equal(1, s.DependencyLookups);
    }

    [Fact]
    public async Task Package_without_a_CMS_dependency_is_NotCmsDependent()
    {
        var s = new FakeSource().Add("Serilog", "4.2.0", ("System.Diagnostics.DiagnosticSource", "8.0.0"));

        var r = await Check(s, "Serilog", "4.2.0");

        Assert.Equal(PackageCompatStatus.NotCmsDependent, r.Status);
    }

    [Fact]
    public async Task EPiServer_Framework_alone_is_not_treated_as_a_CMS_anchor()
    {
        // Framework's version numbers do not track the CMS major, so a cap on it proves nothing.
        var s = new FakeSource().Add("Fw.Addon", "1.0.0", ("EPiServer.Framework", "[12.0.0, 13.0.0)"));

        var r = await Check(s, "Fw.Addon", "1.0.0");

        Assert.Equal(PackageCompatStatus.NotCmsDependent, r.Status);
    }

    [Fact]
    public async Task Unknown_package_is_Unknown()
    {
        var r = await Check(new FakeSource(), "Private.Pkg", "1.0.0");

        Assert.Equal(PackageCompatStatus.Unknown, r.Status);
        Assert.NotNull(r.Reason);
        Assert.Contains("Private.Pkg", r.Reason);
        Assert.Contains("not found on any configured NuGet feed", r.Reason);
    }

    [Fact]
    public async Task CMS_platform_packages_are_skipped_without_any_feed_lookup()
    {
        var s = new FakeSource();

        var r = await Check(s, "EPiServer.CMS.UI", "12.34.2");

        Assert.Equal(PackageCompatStatus.NotCmsDependent, r.Status);
        Assert.Equal(0, s.DependencyLookups);
    }

    [Fact]
    public async Task Findings_map_status_to_severity_and_skip_compatible_packages()
    {
        var project = new ProjectContext(@"C:\p\App.csproj", @"C:\p", Array.Empty<string>(),
            Array.Empty<FileContext>(), Array.Empty<FileContext>(),
            ProjectFileContent: "<Project>\n<PackageReference Include=\"Geta.X\" Version=\"1.0.0\" />\n</Project>");

        var upgrade = PackageCompatibilityFindings.ToFinding(
            new PackageCompatResult("Geta.X", "1.0.0", PackageCompatStatus.UpgradeAvailable, "2.0.0", "2.1.0"), project, 13)!;
        Assert.Equal(Severity.Warning, upgrade.Severity);
        Assert.Equal(2, upgrade.LineNumber);
        Assert.Contains("2.0.0", upgrade.Message);

        Assert.Equal(Severity.Info, PackageCompatibilityFindings.ToFinding(
            new PackageCompatResult("Geta.X", "1.0.0", PackageCompatStatus.Unknown), project, 13)!.Severity);
        Assert.Null(PackageCompatibilityFindings.ToFinding(
            new PackageCompatResult("Geta.X", "1.0.0", PackageCompatStatus.Compatible), project, 13));
    }
}

public class PackageCompatibilityRangeTests
{
    [Theory]
    [InlineData("[13.0.0, 14.0.0)", true)]
    [InlineData("[13.3.0, 14.0.0)", true)]   // min above 13.0.0 still targets the 13 line
    [InlineData("13.3.0", true)]             // open-ended minimum
    [InlineData("[12.0.0, 13.0.0)", false)]  // capped below 13
    [InlineData("[12.0.0, 13.0.0]", false)]  // inclusive ceiling at 13.0.0 caps the 12 line (e.g. Commerce 14.x)
    [InlineData("[13.0.0, 13.0.0]", true)]   // exact pin inside the 13 line
    [InlineData("[14.0.0, )", false)]        // starts after 13
    public async Task Range_admission_uses_the_target_major_line(string range, bool expected)
    {
        var source = new RangeSource(range);
        var checker = new PackageCompatibilityChecker(source);

        var result = await checker.CheckAsync("Addon", "1.0.0", CancellationToken.None);

        Assert.Equal(expected ? PackageCompatStatus.Compatible : PackageCompatStatus.NoCompatibleRelease, result.Status);
    }

    private sealed class RangeSource : IPackageMetadataSource
    {
        private readonly string _range;
        public RangeSource(string range) => _range = range;

        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(new[] { NuGetVersion.Parse("1.0.0") });

        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(
                new[] { new PackageDependency("EPiServer.CMS.Core", VersionRange.Parse(_range)) });
    }
}

public class PackageCompatibilityTransitiveTests
{
    private sealed class MapSource : IPackageMetadataSource
    {
        private readonly Dictionary<string, (string[] Versions, Dictionary<string, (string Id, string Range)[]> Deps)> _map = new(StringComparer.OrdinalIgnoreCase);
        public MapSource Add(string id, string version, params (string Id, string Range)[] deps)
        {
            if (!_map.TryGetValue(id, out var e)) _map[id] = e = (Array.Empty<string>(), new());
            e.Deps[version] = deps;
            return this;
        }
        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(_map.TryGetValue(id, out var e) ? e.Deps.Keys.Select(NuGetVersion.Parse).ToList() : null);
        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(
                _map.TryGetValue(id, out var e) && e.Deps.TryGetValue(v.ToNormalizedString(), out var d)
                    ? d.Select(x => new PackageDependency(x.Id, VersionRange.Parse(x.Range))).ToList()
                    : null);
    }

    [Fact]
    public async Task Compatibility_is_inherited_from_a_first_party_dependency()
    {
        var s = new MapSource()
            .Add("EPiServer.ImageLibrary.ImageSharp", "2.0.5", ("EPiServer.ImageLibrary", "[2.0.0, 2.0.0]"))
            .Add("EPiServer.ImageLibrary.ImageSharp", "13.3.0", ("EPiServer.ImageLibrary", "[13.3.0, 13.3.0]"))
            .Add("EPiServer.ImageLibrary", "2.0.0", ("EPiServer.CMS.Core", "[12.0.0, 13.0.0)"))
            .Add("EPiServer.ImageLibrary", "13.3.0", ("EPiServer.CMS.Core", "[13.3.0, 14.0.0)"));

        var r = await new PackageCompatibilityChecker(s).CheckAsync("EPiServer.ImageLibrary.ImageSharp", "2.0.5", CancellationToken.None);

        Assert.Equal(PackageCompatStatus.UpgradeAvailable, r.Status);
        Assert.Equal("13.3.0", r.FirstCompatibleVersion);
    }
}

public class PackageCompatibilityFrameworkSignalTests
{
    private sealed class FwSource : IPackageMetadataSource
    {
        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(new[] { NuGetVersion.Parse("2.0.5"), NuGetVersion.Parse("13.3.0") });

        // 2.0.5 is capped below CMS 13; 13.3.0 declares no CMS dependency at all.
        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(v.Major < 13
                ? new[] { new PackageDependency("EPiServer.CMS.Core", VersionRange.Parse("[12.0.0, 13.0.0)")) }
                : Array.Empty<PackageDependency>());

        public Task<bool> TargetsOnlyDotNetAsync(string id, NuGetVersion v, int minMajor, CancellationToken ct) =>
            Task.FromResult(v.Major >= 13); // 13.3.0 targets net10.0 only
    }

    [Fact]
    public async Task First_party_package_targeting_only_net10_counts_as_supporting_the_target()
    {
        var r = await new PackageCompatibilityChecker(new FwSource())
            .CheckAsync("EPiServer.SomeLibrary", "2.0.5", CancellationToken.None);

        Assert.Equal(PackageCompatStatus.UpgradeAvailable, r.Status);
        Assert.Equal("13.3.0", r.FirstCompatibleVersion);
    }

    [Fact]
    public async Task Third_party_package_is_never_judged_by_framework_alone_and_is_not_CMS_dependent()
    {
        var r = await new PackageCompatibilityChecker(new FwSource())
            .CheckAsync("Some.Library", "13.3.0", CancellationToken.None);

        Assert.Equal(PackageCompatStatus.NotCmsDependent, r.Status);
    }
}

public class PackageCompatibilityUnknownVersionTests
{
    private sealed class PlainLibrarySource : IPackageMetadataSource
    {
        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(new[] { NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0") });

        // A generic library: no CMS dependency in any version.
        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(Array.Empty<PackageDependency>());
    }

    [Fact]
    public async Task Generic_library_with_unknown_installed_version_is_not_reported_as_incompatible()
    {
        var r = await new PackageCompatibilityChecker(new PlainLibrarySource())
            .CheckAsync("Serilog", null, CancellationToken.None);

        Assert.Equal(PackageCompatStatus.NotCmsDependent, r.Status);
    }
}

public class PackageCompatibilityNewerNet10Tests
{
    private sealed class OpenRangeSource : IPackageMetadataSource
    {
        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(
                new[] { "3.2.1", "3.5.0", "4.0.0", "4.1.0" }.Select(NuGetVersion.Parse).ToList());

        // Every version has an open-ended CMS dependency, so all pass by declared range.
        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(
                new[] { new PackageDependency("EPiServer.CMS.UI.Core", VersionRange.Parse("12.0.2")) });

        // 4.x targets net10.0 only.
        public Task<bool> TargetsOnlyDotNetAsync(string id, NuGetVersion v, int minMajor, CancellationToken ct) =>
            Task.FromResult(v.Major >= 4);
    }

    [Fact]
    public async Task Compatible_by_range_but_newer_net10_only_release_exists_is_reported_with_the_first_one()
    {
        var r = await new PackageCompatibilityChecker(new OpenRangeSource())
            .CheckAsync("Geta.Optimizely.Sitemaps", "3.2.1", CancellationToken.None);

        Assert.Equal(PackageCompatStatus.CompatibleNewerNet10Available, r.Status);
        Assert.Equal("4.0.0", r.FirstCompatibleVersion);
    }

    [Fact]
    public async Task Already_on_a_net10_only_release_is_plain_Compatible()
    {
        var r = await new PackageCompatibilityChecker(new OpenRangeSource())
            .CheckAsync("Geta.Optimizely.Sitemaps", "4.0.0", CancellationToken.None);

        Assert.Equal(PackageCompatStatus.Compatible, r.Status);
    }

    [Fact]
    public void Hint_is_an_Info_finding_that_names_the_release()
    {
        var project = new ProjectContext(@"C:\p\App.csproj", @"C:\p", Array.Empty<string>(),
            Array.Empty<FileContext>(), Array.Empty<FileContext>(),
            ProjectFileContent: "<PackageReference Include=\"Geta.Optimizely.Sitemaps\" Version=\"3.2.1\" />");

        var f = PackageCompatibilityFindings.ToFinding(
            new PackageCompatResult("Geta.Optimizely.Sitemaps", "3.2.1", PackageCompatStatus.CompatibleNewerNet10Available, "4.0.0", "4.1.0"), project, 13)!;

        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("4.0.0", f.Message);
    }
}

public class PackageCompatibilityUnknownReasonTests
{
    private sealed class KnownPackageMissingVersionSource : IPackageMetadataSource
    {
        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(new[] { NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("1.2.0") });

        // No version ever has dependency info available - simulates "the referenced version was
        // never published", since 2.3.0 (what gets checked below) is not even in the version list.
        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(null);
    }

    [Fact]
    public async Task Referenced_version_missing_from_the_known_version_list_names_the_version_and_the_latest_known_one()
    {
        // Regression guard for a real false alarm: a scan referencing a made-up/stale version of a
        // package that genuinely exists on the feed previously produced the exact same generic
        // "not found on the configured feeds" message as a package that does not exist anywhere -
        // indistinguishable from "this whole package is unknown to the tool".
        var r = await new PackageCompatibilityChecker(new KnownPackageMissingVersionSource())
            .CheckAsync("EPiServer.Labs.ContentManager", "2.3.0", CancellationToken.None);

        Assert.Equal(PackageCompatStatus.Unknown, r.Status);
        Assert.NotNull(r.Reason);
        Assert.Contains("2.3.0", r.Reason);
        Assert.Contains("was not found on any configured feed", r.Reason);
        Assert.Contains("1.2.0", r.Reason); // names the latest version that IS known, for comparison
    }

    private sealed class KnownVersionButDependencyInfoUnavailableSource : IPackageMetadataSource
    {
        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(new[] { NuGetVersion.Parse("1.2.0") });

        // The version IS published (it's in the list above) but the feed won't return its
        // dependency metadata - a feed-side problem, not a stale/mistyped reference.
        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(null);
    }

    [Fact]
    public async Task Published_version_whose_dependency_info_cannot_be_read_gets_a_different_reason_than_a_missing_version()
    {
        var r = await new PackageCompatibilityChecker(new KnownVersionButDependencyInfoUnavailableSource())
            .CheckAsync("Some.Package", "1.2.0", CancellationToken.None);

        Assert.Equal(PackageCompatStatus.Unknown, r.Status);
        Assert.NotNull(r.Reason);
        Assert.Contains("even though that version is published", r.Reason);
        Assert.DoesNotContain("was not found on any configured feed", r.Reason);
    }
}

public class PackageCompatibilityCacheTests
{
    private sealed class FlakySource : IPackageMetadataSource
    {
        private int _calls;

        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("simulated transient feed failure");
            return Task.FromResult<IReadOnlyList<NuGetVersion>?>(new[] { NuGetVersion.Parse("1.0.0") });
        }

        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(Array.Empty<PackageDependency>());
    }

    [Fact]
    public async Task A_failed_check_is_not_permanently_cached_and_a_later_call_retries()
    {
        // Regression guard: CheckAsync used to cache the Task itself keyed by (id, version)
        // regardless of how it completed. A package whose check failed or was cancelled by one
        // project's timeout would then return that same faulted/cancelled task forever to every
        // later caller for the same package+version - including unrelated projects in the same
        // solution scan - never retrying even though the failure was transient.
        var source = new FlakySource();
        var checker = new PackageCompatibilityChecker(source);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckAsync("Flaky.Pkg", "1.0.0", CancellationToken.None));

        var result = await checker.CheckAsync("Flaky.Pkg", "1.0.0", CancellationToken.None);

        Assert.Equal(PackageCompatStatus.NotCmsDependent, result.Status);
    }

    private sealed class CountingSuccessSource : IPackageMetadataSource
    {
        public int DependencyLookups;

        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(new[] { NuGetVersion.Parse("1.0.0") });

        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct)
        {
            DependencyLookups++;
            return Task.FromResult<IReadOnlyList<PackageDependency>?>(
                new[] { new PackageDependency("EPiServer.CMS.Core", VersionRange.Parse("[13.0.0, 14.0.0)")) });
        }
    }

    [Fact]
    public async Task A_successful_check_is_still_cached_and_not_recomputed()
    {
        var source = new CountingSuccessSource();
        var checker = new PackageCompatibilityChecker(source);

        await checker.CheckAsync("Geta.X", "1.0.0", CancellationToken.None);
        await checker.CheckAsync("Geta.X", "1.0.0", CancellationToken.None);

        Assert.Equal(1, source.DependencyLookups);
    }
}

public class PackageCompatibilityConcurrencyTests
{
    private sealed class ConcurrencyTrackingSource : IPackageMetadataSource
    {
        private int _concurrent;
        private readonly object _gate = new();
        public int MaxObservedConcurrency { get; private set; }

        public async Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct)
        {
            lock (_gate)
            {
                _concurrent++;
                if (_concurrent > MaxObservedConcurrency) MaxObservedConcurrency = _concurrent;
            }
            await Task.Delay(15, ct);
            lock (_gate) { _concurrent--; }
            return new[] { NuGetVersion.Parse("1.0.0") };
        }

        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(Array.Empty<PackageDependency>());
    }

    [Fact]
    public async Task Many_distinct_packages_checked_at_once_are_all_correct_and_concurrency_is_bounded()
    {
        // A project with many PackageReferences previously fired one unthrottled request per
        // package simultaneously, risking feed rate-limiting (whose failures were in turn
        // silently swallowed and reported as misleading "Unknown" results).
        var source = new ConcurrencyTrackingSource();
        var checker = new PackageCompatibilityChecker(source);

        var ids = Enumerable.Range(0, 30).Select(i => $"Pkg.{i}").ToList();
        var results = await Task.WhenAll(ids.Select(id => checker.CheckAsync(id, "1.0.0", CancellationToken.None)));

        Assert.All(results, r => Assert.Equal(PackageCompatStatus.NotCmsDependent, r.Status));
        Assert.True(source.MaxObservedConcurrency <= 8,
            $"expected concurrency throttled to <= 8, observed {source.MaxObservedConcurrency}");
    }
}
