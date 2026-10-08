namespace OptimizelyCms13ReadinessScanner.Tests;

public class ReviewFixesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cms13-scan-review-" + Guid.NewGuid().ToString("N"));

    public ReviewFixesTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\" />";
    private const string Offending = "class C { void M(dynamic a) { var x = a.FilteredItems; } }";

    private string Project(string relativeDir, string name)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), Csproj);
        File.WriteAllText(Path.Combine(dir, "A.cs"), Offending);
        return Path.Combine(dir, name + ".csproj");
    }

    // ---- rule definition validation -------------------------------------------------------

    private string RulesFile(string json)
    {
        var path = Path.Combine(_root, "rules-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Theory]
    [InlineData("""{"rules":[{"id":"X-1","title":"t","severity":"Warning","kind":"Nope"}]}""", "unknown kind")]
    [InlineData("""{"rules":[{"id":"X-1","title":"t","severity":"Warning","kind":"IdentifierUsage"}]}""", "'identifiers' is required")]
    [InlineData("""{"rules":[{"id":"X-1","title":"t","severity":"Warning","kind":"MemberAccessPattern"}]}""", "'memberNamePrefix' is required")]
    [InlineData("""{"rules":[{"title":"t","severity":"Warning","kind":"IdentifierUsage","identifiers":["A"]}]}""", "missing id")]
    public void Invalid_custom_rule_is_skipped_with_a_warning_and_never_crashes_the_scan(string json, string expectedWarning)
    {
        var warnings = new List<string>();
        Project("app", "App");

        var engine = new ScannerEngine(rulesManifestPath: RulesFile(json), warn: warnings.Add);
        var summary = engine.Scan(_root); // must not throw

        Assert.Contains(warnings, w => w.Contains(expectedWarning));
        Assert.DoesNotContain(summary.Findings, f => f.RuleId == "X-1");
    }

    [Fact]
    public void Valid_custom_rule_still_runs_alongside_an_invalid_one()
    {
        Project("app", "App");
        var json = """
            {"rules":[
              {"id":"BAD-1","title":"t","severity":"Warning","kind":"Nope"},
              {"id":"ACME-001","title":"No Foo","severity":"Warning","kind":"IdentifierUsage","identifiers":["FilteredItems"],"message":"found {match}"}
            ]}
            """;

        var summary = new ScannerEngine(rulesManifestPath: RulesFile(json), warn: _ => { }).Scan(_root);

        Assert.Contains(summary.Findings, f => f.RuleId == "ACME-001");
    }

    // ---- solution parsing -----------------------------------------------------------------

    [Fact]
    public void Scanning_a_sln_only_includes_the_projects_it_lists()
    {
        Project("src/Listed", "Listed");
        Project("sample/NotListed", "NotListed");
        var sln = Path.Combine(_root, "App.sln");
        File.WriteAllText(sln,
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Listed\", \"src/Listed/Listed.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\r\n" +
            "EndProject\r\n" +
            "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"src\", \"src\", \"{22222222-2222-2222-2222-222222222222}\"\r\n" +
            "EndProject\r\n");

        var summary = new ScannerEngine().Scan(sln);

        Assert.Equal(1, summary.TotalProjectFiles);
        Assert.All(summary.Findings.Where(f => f.RuleId == "OPT13-003"), f => Assert.StartsWith("src/Listed/", f.FilePath));
    }

    [Fact]
    public void Scanning_a_slnx_only_includes_the_projects_it_lists()
    {
        Project("src/Listed", "Listed");
        Project("other/NotListed", "NotListed");
        var slnx = Path.Combine(_root, "App.slnx");
        File.WriteAllText(slnx, "<Solution><Folder Name=\"/src/\"><Project Path=\"src/Listed/Listed.csproj\" /></Folder></Solution>");

        var summary = new ScannerEngine().Scan(slnx);

        Assert.Equal(1, summary.TotalProjectFiles);
    }

    [Fact]
    public void A_sln_that_lists_no_csproj_falls_back_to_scanning_the_folder()
    {
        Project("src/App", "App");
        var sln = Path.Combine(_root, "Empty.sln");
        File.WriteAllText(sln, "Microsoft Visual Studio Solution File, Format Version 12.00\r\n");

        var summary = new ScannerEngine().Scan(sln);

        Assert.Equal(1, summary.TotalProjectFiles);
    }

    // ---- folders named "packages" ---------------------------------------------------------

    [Fact]
    public void A_project_under_a_folder_named_packages_is_scanned()
    {
        Project("packages/Lib", "Lib");

        var summary = new ScannerEngine().Scan(_root);

        Assert.Equal(1, summary.TotalProjectFiles);
    }

    // ---- suppression of custom rule ids ---------------------------------------------------

    [Fact]
    public void Suppressing_a_custom_rule_id_does_not_also_suppress_other_rules_on_the_line()
    {
        var dir = Path.Combine(_root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.csproj"), Csproj);
        // Both the custom rule and OPT13-003 fire on this line; only the custom one is disabled.
        File.WriteAllText(Path.Combine(dir, "A.cs"),
            "class C { void M(dynamic a) { var x = a.FilteredItems; /* cms13-scan:disable ACME-001 */ } }");
        var json = """{"rules":[{"id":"ACME-001","title":"No Foo","severity":"Warning","kind":"IdentifierUsage","identifiers":["FilteredItems"],"message":"found"}]}""";

        var summary = new ScannerEngine(rulesManifestPath: RulesFile(json), warn: _ => { }).Scan(_root);

        Assert.DoesNotContain(summary.Findings, f => f.RuleId == "ACME-001");
        Assert.Contains(summary.SuppressedFindingsOrEmpty, f => f.RuleId == "ACME-001");
        Assert.Contains(summary.Findings, f => f.RuleId == "OPT13-003");
    }

    [Fact]
    public void A_ticket_reference_in_the_comment_is_not_mistaken_for_a_rule_id()
    {
        var index = SuppressionIndex.Build(
            new[] { ("f.cs", "x // cms13-scan:disable OPT13-003 -- false positive, see JIRA-123") },
            new[] { "OPT13-003", "OPT13-004" });

        Assert.True(index.IsSuppressed("f.cs", 1, "OPT13-003"));
        Assert.False(index.IsSuppressed("f.cs", 1, "OPT13-004"));
    }
}
