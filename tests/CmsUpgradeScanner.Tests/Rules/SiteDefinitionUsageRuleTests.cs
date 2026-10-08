using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class SiteDefinitionUsageRuleTests
{
    private readonly IUpgradeRule _rule = BuiltInRule.Load("OPT13-008");

    [Fact]
    public void Flags_ISiteDefinitionRepository_and_SiteDefinition_usage_as_a_single_grouped_finding_per_file()
    {
        // OPT13-008 uses groupPerFile: true (see rules.json), so both distinct identifiers used in
        // this one file collapse into a single finding rather than one per occurrence.
        var ctx = ProjectContextBuilder.FromSource(
            """
            class C
            {
                private readonly ISiteDefinitionRepository _repo;
                void M() { var s = SiteDefinition.Current; }
            }
            """, semantic: false);

        var findings = _rule.Evaluate(ctx).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("ISiteDefinitionRepository", finding.Message);
        Assert.Contains("SiteDefinition", finding.Message);
        Assert.Contains("2 occurrence", finding.Message);
        Assert.Equal(3, finding.LineNumber); // the file's first matching line
    }

    [Fact]
    public void Does_not_flag_unrelated_identifiers()
    {
        var ctx = ProjectContextBuilder.FromSource("class C { void M() { var x = SiteSettings.Current; } }", semantic: false);

        Assert.Empty(_rule.Evaluate(ctx));
    }

    [Fact]
    public void A_file_with_many_occurrences_still_produces_exactly_one_finding()
    {
        // Regression guard: a widely-used identifier like SiteDefinition.Current previously
        // produced one finding PER call site - a file with dozens of references flooded the
        // report and buried every other finding in a PR annotation view. groupPerFile caps this
        // at exactly one finding per file regardless of occurrence count, while still preserving
        // the count in the message so the information isn't silently lost.
        var methods = string.Join("\n", Enumerable.Range(0, 40)
            .Select(i => $"    string M{i}() => SiteDefinition.Current.Name;"));
        var source = $"using EPiServer.Web;\nclass Svc\n{{\n{methods}\n}}\n";

        var ctx = ProjectContextBuilder.FromSource(source, semantic: false);

        var finding = Assert.Single(_rule.Evaluate(ctx));
        Assert.Contains("40 occurrence", finding.Message);
    }

    [Fact]
    public void Two_different_files_each_get_their_own_grouped_finding()
    {
        var project = ProjectContextBuilder.FromSource("class A { void M() { var s = SiteDefinition.Current; } }", semantic: false, fileName: "A.cs");
        var projectB = ProjectContextBuilder.FromSource("class B { void M() { var s = SiteDefinition.Current; } }", semantic: false, fileName: "B.cs");

        Assert.Single(_rule.Evaluate(project));
        Assert.Single(_rule.Evaluate(projectB));
    }
}
