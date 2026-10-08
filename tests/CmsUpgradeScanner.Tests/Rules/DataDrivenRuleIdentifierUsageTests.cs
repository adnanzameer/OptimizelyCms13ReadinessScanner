using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

/// <summary>
/// Tests the IdentifierUsage kind's GroupPerFile behavior directly against a hand-built
/// RuleDefinition, independent of the embedded manifest - these assert the general contract any
/// --rules author relying on this kind can depend on, not just the specific OPT13-008 instance.
/// </summary>
public class DataDrivenRuleIdentifierUsageTests
{
    private static RuleDefinition Def(bool groupPerFile) => new(
        Id: "TEST-001", Title: "Test Rule", Severity: Severity.Warning, Kind: "IdentifierUsage",
        Message: "'{match}' found ({count}).", Fix: "fix it",
        Identifiers: new List<string> { "Foo", "Bar" },
        GroupPerFile: groupPerFile);

    // Each usage on its own line: the rule dedupes matches by (line, identifier text), so two
    // occurrences of the same identifier sharing one line would otherwise collapse into one -
    // that dedup is intentional (pre-existing) behaviour, not what these tests are about.
    private const string ThreeOccurrencesAcrossTwoIdentifiers = """
        class C
        {
            void M()
            {
                var a = Foo.X;
                var b = Bar.Y;
                var c = Foo.Z;
            }
        }
        """;

    [Fact]
    public void Default_GroupPerFile_false_emits_one_finding_per_occurrence()
    {
        var rule = new DataDrivenRule(Def(groupPerFile: false));
        var ctx = ProjectContextBuilder.FromSource(ThreeOccurrencesAcrossTwoIdentifiers, semantic: false);

        var findings = rule.Evaluate(ctx).ToList();

        Assert.Equal(3, findings.Count);
        Assert.All(findings, f => Assert.Contains("(1)", f.Message));
    }

    [Fact]
    public void GroupPerFile_true_collapses_all_occurrences_in_a_file_to_one_finding()
    {
        var rule = new DataDrivenRule(Def(groupPerFile: true));
        var ctx = ProjectContextBuilder.FromSource(ThreeOccurrencesAcrossTwoIdentifiers, semantic: false);

        var finding = Assert.Single(rule.Evaluate(ctx));

        Assert.Contains("Bar", finding.Message);
        Assert.Contains("Foo", finding.Message);
        Assert.Contains("(3)", finding.Message); // 3 total occurrences across both identifiers
    }

    [Fact]
    public void GroupPerFile_true_reports_the_first_matching_line()
    {
        var rule = new DataDrivenRule(Def(groupPerFile: true));
        var ctx = ProjectContextBuilder.FromSource(
            "class C\n{\n    void M()\n    {\n        var a = 1;\n        var b = Foo.X;\n        var c = Bar.Y;\n    }\n}", semantic: false);

        var finding = Assert.Single(rule.Evaluate(ctx));

        Assert.Equal(6, finding.LineNumber);
    }

    [Fact]
    public void GroupPerFile_true_with_no_matches_yields_no_finding()
    {
        var rule = new DataDrivenRule(Def(groupPerFile: true));
        var ctx = ProjectContextBuilder.FromSource("class C { void M() { var a = Unrelated.X; } }", semantic: false);

        Assert.Empty(rule.Evaluate(ctx));
    }
}
