namespace OptimizelyCms13ReadinessScanner.Tests;

public class BaselineComparerTests
{
    private static Finding F(string ruleId, string file, int line, Severity severity = Severity.Blocker) =>
        new(ruleId, "Title", severity, "msg", file, line, "fix");

    private static ScanSummary Summary(string targetPath, params Finding[] findings) => new(
        targetPath, DateTime.UtcNow, findings.Length, 1, 1,
        findings.Count(f => f.Severity == Severity.Blocker),
        findings.Count(f => f.Severity == Severity.Warning),
        findings.Count(f => f.Severity == Severity.Info),
        0, findings);

    [Fact]
    public void Findings_present_in_baseline_are_tagged_known_and_excluded_from_new_counts()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-baseline-project");
        var file = Path.Combine(root, "Legacy.cs");
        var baseline = Summary(root, F("OPT13-002", file, 10));
        var current = Summary(root, F("OPT13-002", file, 10));

        var result = BaselineComparer.Apply(current, baseline);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(true, finding.IsBaseline);
        Assert.Equal(0, result.NewBlockersCount);
        Assert.True(result.BaselineApplied);
    }

    [Fact]
    public void Findings_not_in_baseline_are_tagged_new_and_counted()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-baseline-project");
        var file = Path.Combine(root, "Legacy.cs");
        var baseline = Summary(root); // empty baseline
        var current = Summary(root, F("OPT13-002", file, 10));

        var result = BaselineComparer.Apply(current, baseline);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(false, finding.IsBaseline);
        Assert.Equal(1, result.NewBlockersCount);
    }

    [Fact]
    public void Matching_is_insensitive_to_the_absolute_scan_root_moving()
    {
        // Simulates re-running the scanner from a different checkout path, e.g. a local machine
        // vs. a CI runner's workspace - only the path *relative to each run's own TargetPath*
        // should matter, not the absolute prefix.
        // Real, OS-appropriate absolute roots: a drive-letter literal is not a rooted path on Linux,
        // which would make the two "different" roots compare as unrelated relative strings.
        var baselineRoot = Path.Combine(Path.GetTempPath(), "cms13-ci-runner", "work", "checkout1");
        var currentRoot = Path.Combine(Path.GetTempPath(), "cms13-local", "projects", "sample");
        var baseline = Summary(baselineRoot, F("OPT13-003", Path.Combine(baselineRoot, "Web", "Models", "Legacy.cs"), 9, Severity.Warning));
        var current = Summary(currentRoot, F("OPT13-003", Path.Combine(currentRoot, "Web", "Models", "Legacy.cs"), 9, Severity.Warning));

        var result = BaselineComparer.Apply(current, baseline);

        Assert.Equal(true, Assert.Single(result.Findings).IsBaseline);
    }

    [Fact]
    public void Duplicate_findings_on_the_same_line_are_matched_by_count_not_dropped()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-baseline-project");
        var file = Path.Combine(root, "Legacy.cs");
        // sample/Legacy.cs:10 is a real example of this: two distinct OPT13-002 findings land on
        // the exact same line (GetContentResult + SearchClient.Instance).
        var baseline = Summary(root, F("OPT13-002", file, 10), F("OPT13-002", file, 10));
        var current = Summary(root, F("OPT13-002", file, 10), F("OPT13-002", file, 10), F("OPT13-002", file, 10));

        var result = BaselineComparer.Apply(current, baseline);

        Assert.Equal(2, result.Findings.Count(f => f.IsBaseline == true));
        Assert.Equal(1, result.Findings.Count(f => f.IsBaseline == false));
        Assert.Equal(1, result.NewBlockersCount);
    }

    [Fact]
    public void Resolved_findings_that_only_exist_in_the_baseline_simply_disappear()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-baseline-project");
        var file = Path.Combine(root, "Legacy.cs");
        var baseline = Summary(root, F("OPT13-002", file, 10));
        var current = Summary(root); // the issue was fixed; nothing found this run

        var result = BaselineComparer.Apply(current, baseline);

        Assert.Empty(result.Findings);
        Assert.Equal(0, result.NewBlockersCount);
    }

    [Fact]
    public void A_different_rule_on_the_same_line_as_a_baseline_finding_still_counts_as_new()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-baseline-project");
        var file = Path.Combine(root, "Legacy.cs");
        var baseline = Summary(root, F("OPT13-002", file, 10));
        var current = Summary(root, F("OPT13-002", file, 10), F("OPT13-004", file, 10, Severity.Warning));

        var result = BaselineComparer.Apply(current, baseline);

        Assert.Equal(true, result.Findings.Single(f => f.RuleId == "OPT13-002").IsBaseline);
        Assert.Equal(false, result.Findings.Single(f => f.RuleId == "OPT13-004").IsBaseline);
    }

    [Fact]
    public void TryLoad_round_trips_a_real_JsonReporter_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-baseline-project");
        var original = Summary(root, F("OPT13-002", Path.Combine(root, "Legacy.cs"), 10));
        var json = JsonReporter.Generate(original);

        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, json);

            var loaded = BaselineComparer.TryLoad(tempFile, out var baseline, out var error);

            Assert.True(loaded);
            Assert.Null(error);
            Assert.NotNull(baseline);
            Assert.Single(baseline!.Findings);
            Assert.Equal("OPT13-002", baseline.Findings[0].RuleId);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void TryLoad_fails_gracefully_for_a_missing_file()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".json");

        var loaded = BaselineComparer.TryLoad(missingPath, out var baseline, out var error);

        Assert.False(loaded);
        Assert.Null(baseline);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryLoad_fails_gracefully_for_invalid_json()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "{ not valid json ");

            var loaded = BaselineComparer.TryLoad(tempFile, out var baseline, out var error);

            Assert.False(loaded);
            Assert.Null(baseline);
            Assert.NotNull(error);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
