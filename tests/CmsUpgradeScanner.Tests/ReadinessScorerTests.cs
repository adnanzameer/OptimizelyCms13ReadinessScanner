namespace OptimizelyCms13ReadinessScanner.Tests;

public class ReadinessScorerTests
{
    private static Finding F(string ruleId, Severity severity) =>
        new(ruleId, "Title", severity, "msg", "File.cs", 1, "fix");

    [Fact]
    public void No_findings_scores_100()
    {
        Assert.Equal(100, ReadinessScorer.Score(Array.Empty<Finding>()));
    }

    [Fact]
    public void Score_is_based_on_distinct_rules_not_occurrence_count()
    {
        // 50 occurrences of the SAME rule should cost exactly as much as 1 occurrence of it -
        // this is the core fix for "every real codebase floors to 0% within the first 20 hits."
        var manyOccurrences = Enumerable.Range(0, 50).Select(_ => F("OPT13-002", Severity.Blocker)).ToList();
        var oneOccurrence = new List<Finding> { F("OPT13-002", Severity.Blocker) };

        Assert.Equal(ReadinessScorer.Score(oneOccurrence), ReadinessScorer.Score(manyOccurrences));
    }

    [Fact]
    public void More_distinct_rules_costs_more_than_fewer_distinct_rules()
    {
        var oneRule = new List<Finding> { F("OPT13-001", Severity.Blocker) };
        var twoRules = new List<Finding> { F("OPT13-001", Severity.Blocker), F("OPT13-002", Severity.Blocker) };

        Assert.True(ReadinessScorer.Score(twoRules) < ReadinessScorer.Score(oneRule));
    }

    [Fact]
    public void Blockers_cost_more_per_rule_than_warnings_or_info()
    {
        var oneBlockerRule = new List<Finding> { F("OPT13-001", Severity.Blocker) };
        var oneWarningRule = new List<Finding> { F("OPT13-004", Severity.Warning) };
        var oneInfoRule = new List<Finding> { F("OPT13-006", Severity.Info) };

        Assert.True(ReadinessScorer.Score(oneBlockerRule) < ReadinessScorer.Score(oneWarningRule));
        Assert.True(ReadinessScorer.Score(oneWarningRule) < ReadinessScorer.Score(oneInfoRule));
    }

    [Fact]
    public void Score_is_floored_at_zero_and_never_negative()
    {
        var everyRuleImaginable = Enumerable.Range(0, 20)
            .Select(i => F($"OPT13-{i:000}", Severity.Blocker))
            .ToList();

        Assert.Equal(0, ReadinessScorer.Score(everyRuleImaginable));
    }
}
