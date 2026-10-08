using System.Text.Json;

namespace OptimizelyCms13ReadinessScanner.Tests;

public class SarifReporterTests
{
    private static ScanSummary SampleSummary(string targetPath, IReadOnlyList<Finding> findings) =>
        new(
            TargetPath: targetPath,
            ScannedAtUtc: DateTime.UtcNow,
            TotalFilesScanned: findings.Count,
            TotalCsharpFiles: 1,
            TotalProjectFiles: 1,
            BlockersCount: findings.Count(f => f.Severity == Severity.Blocker),
            WarningsCount: findings.Count(f => f.Severity == Severity.Warning),
            InfoCount: findings.Count(f => f.Severity == Severity.Info),
            ReadinessScore: 0,
            Findings: findings);

    [Fact]
    public void Produces_valid_sarif_2_1_0_envelope()
    {
        var summary = SampleSummary(Path.GetTempPath(), Array.Empty<Finding>());

        var json = SarifReporter.Generate(summary);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("2.1.0", doc.RootElement.GetProperty("version").GetString());
        Assert.True(doc.RootElement.TryGetProperty("$schema", out _));
        Assert.Equal(1, doc.RootElement.GetProperty("runs").GetArrayLength());
        Assert.Equal("cms13-scan",
            doc.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());
    }

    [Fact]
    public void Maps_severities_to_sarif_levels()
    {
        var file = Path.Combine(Path.GetTempPath(), "Legacy.cs");
        var findings = new[]
        {
            new Finding("OPT13-001", "Title B", Severity.Blocker, "msg", file, 5, "fix"),
            new Finding("OPT13-005", "Title W", Severity.Warning, "msg", file, 3, "fix"),
            new Finding("OPT13-006", "Title I", Severity.Info, "msg", file, 1, "fix"),
        };
        var summary = SampleSummary(Path.GetTempPath(), findings);

        var json = SarifReporter.Generate(summary);
        using var doc = JsonDocument.Parse(json);
        var levels = doc.RootElement.GetProperty("runs")[0].GetProperty("results")
            .EnumerateArray().Select(r => r.GetProperty("level").GetString()).ToList();

        Assert.Contains("error", levels);   // Blocker
        Assert.Contains("warning", levels); // Warning
        Assert.Contains("note", levels);    // Info
    }

    [Fact]
    public void Uses_relative_artifact_uri_under_target_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-scan-sarif-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Models"));
        var file = Path.Combine(root, "Models", "Legacy.cs");

        try
        {
            var findings = new[] { new Finding("OPT13-003", "Title", Severity.Warning, "msg", file, 9, "fix") };
            var summary = SampleSummary(root, findings);

            var json = SarifReporter.Generate(summary);
            using var doc = JsonDocument.Parse(json);
            var location = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
                .GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation");

            Assert.Equal("Models/Legacy.cs", location.GetProperty("uri").GetString());
            Assert.Equal("SRCROOT", location.GetProperty("uriBaseId").GetString());

            var baseIds = doc.RootElement.GetProperty("runs")[0].GetProperty("originalUriBaseIds");
            Assert.True(baseIds.TryGetProperty("SRCROOT", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Falls_back_to_absolute_file_uri_outside_target_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-scan-sarif-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outsideFile = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".cs");

        try
        {
            var findings = new[] { new Finding("OPT13-003", "Title", Severity.Warning, "msg", outsideFile, 1, "fix") };
            var summary = SampleSummary(root, findings);

            var json = SarifReporter.Generate(summary);
            using var doc = JsonDocument.Parse(json);
            var location = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
                .GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation");

            Assert.StartsWith("file:///", location.GetProperty("uri").GetString());
            Assert.False(location.TryGetProperty("uriBaseId", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Rule_catalog_contains_one_entry_per_distinct_rule_id()
    {
        var file = Path.Combine(Path.GetTempPath(), "X.cs");
        var findings = new[]
        {
            new Finding("OPT13-001", "A", Severity.Blocker, "m1", file, 1, "f"),
            new Finding("OPT13-001", "A", Severity.Blocker, "m2", file, 2, "f"),
            new Finding("OPT13-004", "B", Severity.Warning, "m", file, 3, "f"),
        };
        var summary = SampleSummary(Path.GetTempPath(), findings);

        var json = SarifReporter.Generate(summary);
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver").GetProperty("rules")
            .EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();

        Assert.Equal(new[] { "OPT13-001", "OPT13-004" }, ids);
        Assert.Equal(3, doc.RootElement.GetProperty("runs")[0].GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void Message_combines_finding_message_and_suggested_fix()
    {
        var file = Path.Combine(Path.GetTempPath(), "X.cs");
        var findings = new[] { new Finding("OPT13-001", "A", Severity.Blocker, "Something is wrong.", file, 1, "Do this instead.") };
        var summary = SampleSummary(Path.GetTempPath(), findings);

        var json = SarifReporter.Generate(summary);
        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("message").GetProperty("text").GetString();

        Assert.Equal("Something is wrong. Do this instead.", text);
    }

    [Fact]
    public void Emits_baselineState_when_a_baseline_was_applied()
    {
        var file = Path.Combine(Path.GetTempPath(), "X.cs");
        var findings = new[]
        {
            new Finding("OPT13-001", "A", Severity.Blocker, "m", file, 1, "f", IsBaseline: true),
            new Finding("OPT13-002", "B", Severity.Blocker, "m", file, 2, "f", IsBaseline: false),
        };
        var summary = SampleSummary(Path.GetTempPath(), findings) with { BaselineApplied = true };

        var json = SarifReporter.Generate(summary);
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToList();

        Assert.Equal("unchanged", results[0].GetProperty("baselineState").GetString());
        Assert.Equal("new", results[1].GetProperty("baselineState").GetString());
    }

    [Fact]
    public void Omits_baselineState_when_no_baseline_was_applied()
    {
        var file = Path.Combine(Path.GetTempPath(), "X.cs");
        var findings = new[] { new Finding("OPT13-001", "A", Severity.Blocker, "m", file, 1, "f") };
        var summary = SampleSummary(Path.GetTempPath(), findings);

        var json = SarifReporter.Generate(summary);
        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0];

        Assert.False(result.TryGetProperty("baselineState", out _));
    }

    [Fact]
    public void Suppressed_findings_are_included_with_an_inSource_suppression()
    {
        var file = Path.Combine(Path.GetTempPath(), "X.cs");
        var active = new[] { new Finding("OPT13-001", "A", Severity.Blocker, "m1", file, 1, "f") };
        var suppressed = new[] { new Finding("OPT13-002", "B", Severity.Blocker, "m2", file, 2, "f") };
        var summary = SampleSummary(Path.GetTempPath(), active) with { SuppressedFindings = suppressed };

        var json = SarifReporter.Generate(summary);
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToList();

        Assert.Equal(2, results.Count);
        var suppressedResult = results.Single(r => r.GetProperty("ruleId").GetString() == "OPT13-002");
        Assert.False(suppressedResult.TryGetProperty("baselineState", out _));
        var suppressions = suppressedResult.GetProperty("suppressions");
        Assert.Equal(1, suppressions.GetArrayLength());
        Assert.Equal("inSource", suppressions[0].GetProperty("kind").GetString());

        var activeResult = results.Single(r => r.GetProperty("ruleId").GetString() == "OPT13-001");
        Assert.False(activeResult.TryGetProperty("suppressions", out _));
    }

    [Fact]
    public void Rule_catalog_includes_rules_that_only_appear_in_suppressed_findings()
    {
        var file = Path.Combine(Path.GetTempPath(), "X.cs");
        var active = Array.Empty<Finding>();
        var suppressed = new[] { new Finding("OPT13-004", "Obsolete FilterAccess", Severity.Warning, "m", file, 1, "f") };
        var summary = SampleSummary(Path.GetTempPath(), active) with { SuppressedFindings = suppressed };

        var json = SarifReporter.Generate(summary);
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver").GetProperty("rules")
            .EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();

        Assert.Contains("OPT13-004", ids);
    }
}
