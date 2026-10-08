using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests;

/// <summary>
/// End-to-end snapshot test: scans the repo's own sample/ fixture (the same one the README and CI
/// self-scan reference) and compares the JSON report against a committed expected-output file.
/// This replaces the regression-detection value the repo's root-level report.json/.md/.sarif used
/// to provide before they were gitignored (to stop every rule/scoring change from producing
/// machine-path/timestamp diff noise): a behavior change to any rule, the scorer, or the
/// data-driven rule manifest now shows up here as a failing test with a clear diff, instead of
/// silently passing unnoticed with no committed output left to compare against.
///
/// TargetPath and ScannedAtUtc are normalized to fixed placeholder values before comparison, since
/// they are inherently machine-/time-dependent and would make the fixture impossible to commit.
/// Everything else - findings, counts, the readiness score - is fully deterministic for a fixed
/// sample/ fixture and a fixed rule set, run the same way CI's self-scan step runs it (no
/// --semantic, no --check-packages, no --baseline).
///
/// If sample/ or the rule set changes intentionally: run the scan, inspect the new JSON by eye to
/// confirm it reflects the intended change and nothing else, then update
/// Snapshots/sample.expected.json to match (with TargetPath/ScannedAtUtc normalized the same way
/// this test normalizes them) and commit it alongside the change that caused the diff.
/// </summary>
public class SampleFixtureSnapshotTests
{
    [Fact]
    public void Scanning_the_committed_sample_fixture_matches_the_committed_expected_report()
    {
        var sampleDir = RepoPaths.Find("sample");
        var expectedPath = RepoPaths.TestFile("Snapshots", "sample.expected.json");

        var summary = new ScannerEngine().Scan(sampleDir);
        var normalized = summary with { TargetPath = "<SAMPLE_DIR>", ScannedAtUtc = DateTime.MinValue };
        var actualJson = JsonReporter.Generate(normalized);

        Assert.True(File.Exists(expectedPath), $"Expected snapshot file not found at {expectedPath}.");
        var expectedJson = File.ReadAllText(expectedPath);

        // Line-ending-insensitive: the fixture is checked out through the repo's own
        // .gitattributes normalization, which can differ from the in-memory generated string.
        Assert.Equal(Normalize(expectedJson), Normalize(actualJson));
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
