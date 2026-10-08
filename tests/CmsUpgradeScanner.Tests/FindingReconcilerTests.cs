namespace OptimizelyCms13ReadinessScanner.Tests;

public class FindingReconcilerTests
{
    private static Finding F(string rule, string file, int line) =>
        new(rule, "t", Severity.Blocker, "m", file, line, "fix");

    [Fact]
    public void Drops_OPT13_003_only_on_lines_where_the_api_check_also_reported()
    {
        var findings = new List<Finding>
        {
            F("OPT13-003", "/work/A.cs", 10),   // also reported by OPT13-013 -> dropped
            F("OPT13-013", "/work/A.cs", 10),
            F("OPT13-003", "/work/A.cs", 20),   // only the hardcoded rule saw this one -> kept
            F("OPT13-003", "/work/B.cs", 10),   // same line number, different file -> kept
            F("OPT13-004", "/work/A.cs", 10)    // unrelated rule on the same line -> kept
        };

        FindingReconciler.PreferApiCheckOverFilteredItemsRule(findings);

        Assert.Equal(4, findings.Count);
        Assert.DoesNotContain(findings, f => f.RuleId == "OPT13-003" && f.FilePath == "/work/A.cs" && f.LineNumber == 10);
        Assert.Contains(findings, f => f.RuleId == "OPT13-003" && f.LineNumber == 20);
        Assert.Contains(findings, f => f.RuleId == "OPT13-003" && f.FilePath == "/work/B.cs");
    }

    [Fact]
    public void Does_nothing_when_the_api_check_did_not_run()
    {
        var findings = new List<Finding> { F("OPT13-003", "/work/A.cs", 10) };

        FindingReconciler.PreferApiCheckOverFilteredItemsRule(findings);

        Assert.Single(findings);
    }

    [Fact]
    public void Path_casing_differences_do_not_defeat_the_match()
    {
        var findings = new List<Finding> { F("OPT13-003", "/work/a.cs", 5), F("OPT13-013", "/work/A.CS", 5) };

        FindingReconciler.PreferApiCheckOverFilteredItemsRule(findings);

        Assert.Single(findings);
        Assert.Equal("OPT13-013", findings[0].RuleId);
    }
}
