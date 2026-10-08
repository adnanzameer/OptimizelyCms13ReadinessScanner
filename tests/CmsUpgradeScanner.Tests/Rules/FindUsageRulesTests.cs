using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class FindNamespaceUsageRuleTests
{
    private readonly FindNamespaceUsageRule _rule = new();

    [Fact]
    public void Flags_a_file_importing_EPiServer_Find_even_without_any_known_call()
    {
        var ctx = ProjectContextBuilder.FromSource(
            """
            using System;
            using EPiServer.Find;

            class C { IClient _client; }
            """, semantic: false);

        var finding = Assert.Single(_rule.Evaluate(ctx));

        Assert.Equal(Severity.Blocker, finding.Severity);
        Assert.Equal(2, finding.LineNumber);
    }

    [Fact]
    public void Reports_once_per_file_and_counts_the_other_imports()
    {
        var ctx = ProjectContextBuilder.FromSource(
            """
            using EPiServer.Find;
            using EPiServer.Find.Cms;
            using EPiServer.Find.Framework;

            class C { }
            """, semantic: false);

        var finding = Assert.Single(_rule.Evaluate(ctx));

        Assert.Equal(1, finding.LineNumber);
        Assert.Contains("+2 more", finding.Message);
    }

    [Theory]
    [InlineData("using EPiServer.Core;")]
    [InlineData("using EPiServer.FindMe;")]       // prefix of the name, but a different namespace
    [InlineData("using MyApp.EPiServer.Find;")]
    public void Does_not_flag_other_namespaces(string usingLine)
    {
        var ctx = ProjectContextBuilder.FromSource(usingLine + "\nclass C { }", semantic: false);

        Assert.Empty(_rule.Evaluate(ctx));
    }
}

public class TransitiveFindPackageRuleTests
{
    private readonly TransitiveFindPackageRule _rule = new();

    private static ProjectContext Project(string[] direct, Dictionary<string, string[]>? graph)
    {
        var content = "<Project>\n" + string.Join("\n", direct.Select(d => $"<PackageReference Include=\"{d}\" Version=\"1.0.0\" />")) + "\n</Project>";
        return new ProjectContext(@"C:\p\App.csproj", @"C:\p", direct, Array.Empty<FileContext>(), Array.Empty<FileContext>(),
            ProjectFileContent: content,
            PackageGraph: graph?.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flags_a_direct_package_whose_dependency_chain_reaches_Find_and_names_the_chain()
    {
        var ctx = Project(new[] { "Acme.Search", "Serilog" }, new()
        {
            ["Acme.Search"] = new[] { "Acme.Core" },
            ["Acme.Core"] = new[] { "EPiServer.Find.Cms" },
            ["EPiServer.Find.Cms"] = new[] { "EPiServer.Find" },
            ["Serilog"] = Array.Empty<string>()
        });

        var finding = Assert.Single(_rule.Evaluate(ctx));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("Acme.Search -> Acme.Core -> EPiServer.Find.Cms", finding.Message);
        Assert.Equal(2, finding.LineNumber); // Acme.Search's PackageReference line
    }

    [Fact]
    public void Does_not_report_a_direct_Find_reference_because_OPT13_001_owns_that()
    {
        var ctx = Project(new[] { "EPiServer.Find.Cms" }, new() { ["EPiServer.Find.Cms"] = new[] { "EPiServer.Find" } });

        Assert.Empty(_rule.Evaluate(ctx));
    }

    [Fact]
    public void Silent_when_the_project_has_not_been_restored()
    {
        Assert.Empty(_rule.Evaluate(Project(new[] { "Acme.Search" }, graph: null)));
    }

    [Fact]
    public void Survives_a_dependency_cycle()
    {
        var ctx = Project(new[] { "A" }, new() { ["A"] = new[] { "B" }, ["B"] = new[] { "A" } });

        Assert.Empty(_rule.Evaluate(ctx));
    }
}

public class TransitiveFindEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cms13-scan-transitive-" + Guid.NewGuid().ToString("N"));

    public TransitiveFindEndToEndTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Engine_builds_the_dependency_graph_from_a_restored_assets_file()
    {
        var csproj = Path.Combine(_root, "App.csproj");
        File.WriteAllText(csproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>\n" +
            "<PackageReference Include=\"Acme.Search\" Version=\"2.0.0\" />\n" +
            "<PackageReference Include=\"Serilog\" Version=\"4.0.0\" />\n" +
            "</ItemGroup></Project>");

        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        // Shape matches a real restore: libraries (id/version), targets (id/version -> dependencies).
        var assets = new System.Text.StringBuilder();
        assets.Append("{\"version\":3,");
        assets.Append("\"targets\":{\"net8.0\":{");
        assets.Append("\"Acme.Search/2.0.0\":{\"type\":\"package\",\"dependencies\":{\"EPiServer.Find.Cms\":\"16.0.0\"}},");
        assets.Append("\"EPiServer.Find.Cms/16.0.0\":{\"type\":\"package\",\"dependencies\":{\"EPiServer.Find\":\"16.0.0\"}},");
        assets.Append("\"EPiServer.Find/16.0.0\":{\"type\":\"package\"},");
        assets.Append("\"Serilog/4.0.0\":{\"type\":\"package\"}}},");
        assets.Append("\"libraries\":{\"Acme.Search/2.0.0\":{\"type\":\"package\"},\"EPiServer.Find.Cms/16.0.0\":{\"type\":\"package\"},");
        assets.Append("\"EPiServer.Find/16.0.0\":{\"type\":\"package\"},\"Serilog/4.0.0\":{\"type\":\"package\"}},");
        assets.Append("\"project\":{\"restore\":{\"projectPath\":" + System.Text.Json.JsonSerializer.Serialize(csproj) + "}}}");
        File.WriteAllText(Path.Combine(_root, "obj", "project.assets.json"), assets.ToString());

        var summary = new ScannerEngine().Scan(_root);

        var finding = Assert.Single(summary.Findings, f => f.RuleId == "OPT13-012");
        Assert.Contains("Acme.Search -> EPiServer.Find.Cms", finding.Message);
        Assert.DoesNotContain(summary.Findings, f => f.RuleId == "OPT13-012" && f.Message.Contains("Serilog"));
    }
}
