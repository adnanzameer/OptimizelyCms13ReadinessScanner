using NuGet.Packaging.Core;
using NuGet.Versioning;
using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class CmsApiRemovalRuleTests
{
    private const string Cms12 = """
        namespace EPiServer.Core
        {
            public class ContentArea { public object Items => null!; public object FilteredItems => null!; public void Removed() { } }
            public class Gone { }
            public class Kept { }
        }
        """;

    private const string Cms13 = """
        namespace EPiServer.Core
        {
            public class ContentArea { public object Items => null!; }
            public class Kept { }
        }
        """;

    private const string User = """
        using EPiServer.Core;
        class U
        {
            void M(ContentArea a)
            {
                var x = a.FilteredItems;
                var y = a.Items;
                a.Removed();
                var g = new Gone();
                var k = new Kept();
            }
        }
        """;

    private sealed class FakeFeed : IPackageMetadataSource, IPackageContentSource
    {
        public readonly Dictionary<string, string[]> Versions = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Dictionary<string, (string Id, string Range)[]>> Deps = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, byte[]> Content = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Downloads = new();

        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(Versions.TryGetValue(id, out var v) ? v.Select(NuGetVersion.Parse).ToList() : null);

        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion version, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(
                Deps.TryGetValue(id, out var d) && d.TryGetValue(version.ToNormalizedString(), out var list)
                    ? list.Select(x => new PackageDependency(x.Id, VersionRange.Parse(x.Range))).ToList()
                    : Array.Empty<PackageDependency>());

        public Task<IReadOnlyList<byte[]>?> GetAssembliesAsync(string id, NuGetVersion version, CancellationToken ct)
        {
            var key = id + "/" + version.ToNormalizedString();
            lock (Downloads) Downloads.Add(key);   // fetched concurrently
            return Task.FromResult<IReadOnlyList<byte[]>?>(Content.TryGetValue(key, out var image) ? new[] { image } : null);
        }
    }

    private static (CmsApiRemovalRule Rule, ApiSurfaceProvider Provider, FakeFeed Feed) Create(List<string>? warnings = null)
    {
        var feed = new FakeFeed();
        feed.Versions["EPiServer.CMS.Core"] = new[] { "12.0.0", "13.0.0", "13.1.0" };
        feed.Content["EPiServer.CMS.Core/13.0.0"] = StubAssembly.Emit("EPiServer", Cms13);
        var checker = new PackageCompatibilityChecker(feed, 13);
        var provider = new ApiSurfaceProvider(feed, feed, checker, 13, w => warnings?.Add(w));
        return (new CmsApiRemovalRule(provider), provider, feed);
    }

    private static ProjectContext Project(string source, Dictionary<string, string>? restored, byte[] referenced)
    {
        const string dir = "/work/App";
        var (compilation, _) = StubAssembly.CompileUserCode(source, referenced, dir);
        return new ProjectContext(Path.Combine(dir, "App.csproj"), dir, Array.Empty<string>(),
            Array.Empty<FileContext>(), Array.Empty<FileContext>(), Compilation: compilation, AllPackageVersions: restored);
    }

    private static readonly Dictionary<string, string> RestoredCore = new() { ["EPiServer.CMS.Core"] = "12.0.0" };

    [Fact]
    public void Reports_removed_members_and_types_but_not_those_that_still_exist()
    {
        var (rule, _, _) = Create();
        var project = Project(User, RestoredCore, StubAssembly.Emit("EPiServer", Cms12));

        var findings = rule.Evaluate(project).ToList();

        Assert.Equal(3, findings.Count);
        Assert.All(findings, f => Assert.Equal(Severity.Blocker, f.Severity));
        Assert.Contains(findings, f => f.Message.Contains("'FilteredItems' of 'EPiServer.Core.ContentArea'"));
        Assert.Contains(findings, f => f.Message.Contains("'Removed' of 'EPiServer.Core.ContentArea'"));
        Assert.Contains(findings, f => f.Message.Contains("Type 'EPiServer.Core.Gone'"));
        Assert.DoesNotContain(findings, f => f.Message.Contains("Kept"));
    }

    [Fact]
    public void Reports_apis_that_still_exist_in_cms13_but_are_obsolete_there_using_the_attribute_message()
    {
        var (rule, _, feed) = Create();
        feed.Content["EPiServer.CMS.Core/13.0.0"] = StubAssembly.Emit("EPiServer", """
            using System;
            namespace EPiServer.Core
            {
                public class ContentArea
                {
                    [Obsolete("Use Items to get all items.", true)] public object FilteredItems => null!;
                    [Obsolete("Going away.")] public void Soon() { }
                    public object Items => null!;
                }
                [Obsolete("DeadType is gone.", true)] public class DeadType { }
            }
            """);
        const string cms12 = """
            namespace EPiServer.Core
            {
                public class ContentArea { public object FilteredItems => null!; public void Soon() { } public object Items => null!; }
                public class DeadType { }
            }
            """;
        const string user = """
            using EPiServer.Core;
            class U { void M(ContentArea a) { var x = a.FilteredItems; a.Soon(); var y = a.Items; var d = new DeadType(); } }
            """;

        var findings = rule.Evaluate(Project(user, RestoredCore, StubAssembly.Emit("EPiServer", cms12))).ToList();

        Assert.Equal(3, findings.Count);
        var filtered = Assert.Single(findings, f => f.Message.Contains("ContentArea.FilteredItems"));
        Assert.Equal(Severity.Blocker, filtered.Severity);
        Assert.Contains("does not compile", filtered.Message);
        Assert.Equal("Use Items to get all items.", filtered.SuggestedFix);   // Optimizely's own guidance

        var soon = Assert.Single(findings, f => f.Message.Contains("ContentArea.Soon"));
        Assert.Equal(Severity.Warning, soon.Severity);

        Assert.Equal(Severity.Blocker, Assert.Single(findings, f => f.Message.Contains("DeadType")).Severity);
        Assert.DoesNotContain(findings, f => f.Message.Contains("ContentArea.Items"));
    }

    [Fact]
    public void Compares_against_the_lowest_stable_release_of_the_target_major()
    {
        var (rule, _, feed) = Create();

        rule.Evaluate(Project(User, RestoredCore, StubAssembly.Emit("EPiServer", Cms12))).ToList();

        Assert.Equal(new[] { "EPiServer.CMS.Core/13.0.0" }, feed.Downloads);
    }

    [Fact]
    public void Follows_first_party_dependencies_because_CMS13_moved_the_real_types_into_pinned_packages()
    {
        var warnings = new List<string>();
        var (rule, provider, feed) = Create(warnings);
        // As on the real feed: the CMS 13 core package is thin; the types live in "EPiServer" 13.0.0.
        feed.Content["EPiServer.CMS.Core/13.0.0"] = StubAssembly.Emit("EPiServer.CMS.Core", "namespace EPiServer.DependencyInjection { public class Marker { } }");
        feed.Deps["EPiServer.CMS.Core"] = new() { ["13.0.0"] = new[] { ("EPiServer", "[13.0.0, 13.0.0]"), ("Castle.Core", "[5.2.1, 6.0.0)") } };
        feed.Versions["EPiServer"] = new[] { "12.0.0", "13.0.0" };
        feed.Content["EPiServer/13.0.0"] = StubAssembly.Emit("EPiServer", Cms13);

        var findings = rule.Evaluate(Project(User, RestoredCore, StubAssembly.Emit("EPiServer", Cms12))).ToList();

        Assert.Contains("EPiServer/13.0.0", feed.Downloads);
        Assert.DoesNotContain(feed.Downloads, d => d.StartsWith("Castle.Core"));   // third party: not followed
        Assert.Equal(3, findings.Count);                                            // same verdicts as with a flat package
        Assert.Contains("2 assemblies", provider.Summary);
    }

    [Fact]
    public void A_loose_dependency_range_does_not_pull_in_the_old_cms12_build()
    {
        var (rule, _, feed) = Create();
        feed.Content["EPiServer.CMS.Core/13.0.0"] = StubAssembly.Emit("EPiServer", Cms13);
        feed.Deps["EPiServer.CMS.Core"] = new() { ["13.0.0"] = new[] { ("EPiServer.Framework", "[12.0.0, )") } };
        feed.Versions["EPiServer.Framework"] = new[] { "12.23.1", "13.0.0" };
        feed.Content["EPiServer.Framework/13.0.0"] = StubAssembly.Emit("EPiServer.Framework", "namespace EPiServer.Framework { public class F { } }");

        rule.Evaluate(Project(User, RestoredCore, StubAssembly.Emit("EPiServer", Cms12))).ToList();

        Assert.Contains("EPiServer.Framework/13.0.0", feed.Downloads);
        Assert.DoesNotContain("EPiServer.Framework/12.0.0", feed.Downloads);
    }

    [Fact]
    public void Nothing_to_compare_without_a_restored_project_and_it_says_so()
    {
        var warnings = new List<string>();
        var (rule, _, _) = Create(warnings);

        var findings = rule.Evaluate(Project(User, restored: null, StubAssembly.Emit("EPiServer", Cms12))).ToList();

        Assert.Empty(findings);
        Assert.Contains(warnings, w => w.Contains("restored"));
    }

    [Fact]
    public void A_platform_assembly_missing_from_the_index_is_a_Warning()
    {
        var (rule, _, _) = Create();
        const string removedAssembly = "namespace EPiServer.Shell { public class Old { public void Run() { } } }";
        var project = Project("class U { void M() { new EPiServer.Shell.Old().Run(); } }", RestoredCore,
            StubAssembly.Emit("EPiServer.CMS.Shell", removedAssembly));

        var finding = Assert.Single(rule.Evaluate(project), f => f.Message.Contains("Type 'EPiServer.Shell.Old'"));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("may have moved", finding.Message);
    }

    [Fact]
    public void A_non_platform_package_we_have_no_cms13_build_for_is_not_reported()
    {
        var (rule, _, _) = Create();
        var project = Project("class U { void M() { new Acme.Thing().Run(); } }", RestoredCore,
            StubAssembly.Emit("Optimizely.Labs.Thing", "namespace Acme { public class Thing { public void Run() { } } }"));

        Assert.Empty(rule.Evaluate(project));
    }

    [Fact]
    public void Other_packages_are_indexed_from_the_release_the_compatibility_check_picks()
    {
        var (rule, provider, feed) = Create();
        feed.Versions["EPiServer.Forms"] = new[] { "5.10.7", "6.0.0" };
        feed.Deps["EPiServer.Forms"] = new()
        {
            ["5.10.7"] = new[] { ("EPiServer.CMS.UI", "[12.0.0, 13.0.0)") },
            ["6.0.0"] = new[] { ("EPiServer.CMS.UI", "[13.0.0, 14.0.0)") }
        };
        feed.Content["EPiServer.Forms/6.0.0"] = StubAssembly.Emit("EPiServer.Forms", "namespace EPiServer.Forms { public class Form { } }");
        var restored = new Dictionary<string, string>(RestoredCore) { ["EPiServer.Forms"] = "5.10.7", ["Serilog"] = "4.0.0" };

        rule.Evaluate(Project("class U { }", restored, StubAssembly.Emit("EPiServer", Cms12))).ToList();

        Assert.Contains("EPiServer.Forms/6.0.0", feed.Downloads);
        Assert.DoesNotContain(feed.Downloads, d => d.StartsWith("Serilog"));   // not first-party
        Assert.Contains("2 assemblies", provider.Summary);
    }

    [Fact]
    public void A_package_with_no_cms13_build_is_named_in_the_summary()
    {
        var (rule, provider, feed) = Create();
        feed.Versions["EPiServer.Marketing.Testing"] = new[] { "3.1.2" };
        feed.Deps["EPiServer.Marketing.Testing"] = new() { ["3.1.2"] = new[] { ("EPiServer.CMS.UI", "[12.4.0, 13.0.0)") } };
        var restored = new Dictionary<string, string>(RestoredCore) { ["EPiServer.Marketing.Testing"] = "3.1.2" };

        rule.Evaluate(Project("class U { }", restored, StubAssembly.Emit("EPiServer", Cms12))).ToList();

        Assert.Contains("EPiServer.Marketing.Testing", provider.Summary);
    }
}
