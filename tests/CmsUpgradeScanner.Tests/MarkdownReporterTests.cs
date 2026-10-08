namespace OptimizelyCms13ReadinessScanner.Tests;

public class MarkdownReporterTests
{
    private static ScanSummary Summary(params Finding[] findings) => new(
        "/p", DateTime.UtcNow, 1, 1, 1,
        findings.Count(f => f.Severity == Severity.Blocker),
        findings.Count(f => f.Severity == Severity.Warning),
        findings.Count(f => f.Severity == Severity.Info),
        80, findings);

    private static Finding Pkg(string message) =>
        new("OPT13-010", "Third-Party Package CMS 13 Compatibility", Severity.Warning, message, "src/App/App.csproj", 3, "Upgrade it.");

    private static Finding Code() =>
        new("OPT13-003", "Removed Property ContentArea.FilteredItems", Severity.Blocker, "Uses FilteredItems", "src/A.cs", 9, "Fix it.");

    [Fact]
    public void Package_findings_get_their_own_section_and_leave_the_main_table()
    {
        var md = MarkdownReporter.Generate(Summary(Code(), Pkg("'Geta.X 1.0.0' does not support CMS 13, but version 2.0.0 does.")));

        var packageSection = md[md.IndexOf("## Package Compatibility", StringComparison.Ordinal)..];
        Assert.Contains("Geta.X 1.0.0", packageSection);
        Assert.Contains("`App`", packageSection);

        var breakdown = md[md.IndexOf("## Findings Breakdown", StringComparison.Ordinal)..md.IndexOf("## Package Compatibility", StringComparison.Ordinal)];
        Assert.Contains("OPT13-003", breakdown);
        Assert.DoesNotContain("OPT13-010", breakdown);
    }

    [Fact]
    public void No_package_section_when_there_are_no_package_findings()
    {
        var md = MarkdownReporter.Generate(Summary(Code()));

        Assert.DoesNotContain("## Package Compatibility", md);
    }

    [Fact]
    public void Only_package_findings_reports_no_code_issues()
    {
        var md = MarkdownReporter.Generate(Summary(Pkg("x")));

        Assert.Contains("No code or configuration issues detected.", md);
        Assert.Contains("## Package Compatibility", md);
    }

    [Fact]
    public void Package_compatibility_caveat_appears_even_when_every_package_was_compatible()
    {
        // Regression guard: a run where --check-packages found nothing to flag (every direct
        // package was fully "Compatible") previously had NO indication in the report that the
        // check even ran, since the "## Package Compatibility" section only appears when there is
        // at least one OPT13-010 finding to list. The caveat must be visible regardless.
        var summary = Summary() with { PackageCompatibilityChecked = true, PackageCompatibilityTargetMajor = 13 };

        var md = MarkdownReporter.Generate(summary);

        Assert.DoesNotContain("## Package Compatibility", md); // no findings -> no detail section
        Assert.Contains("Package compatibility", md);
        Assert.Contains("--check-packages", md);
        Assert.Contains("CMS 13", md);
        Assert.Contains("not that it was tested", md);
    }

    [Fact]
    public void Package_compatibility_caveat_is_absent_when_check_packages_was_not_used()
    {
        var md = MarkdownReporter.Generate(Summary(Code()));

        Assert.DoesNotContain("Package compatibility", md);
        Assert.DoesNotContain("--check-packages", md);
    }
}
