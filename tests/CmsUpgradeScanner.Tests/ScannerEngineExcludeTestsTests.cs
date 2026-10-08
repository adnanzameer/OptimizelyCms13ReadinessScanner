using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests;

public class ScannerEngineExcludeTestsTests
{
    private const string Offending = "class C { void M(dynamic a) { var x = a.FilteredItems; } }";

    private static void Layout(string root, out string app, out string tests)
    {
        app = Path.Combine(root, "App");
        tests = Path.Combine(root, "App.Tests");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(tests);
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(app, "A.cs"), Offending);
        File.WriteAllText(Path.Combine(tests, "App.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(tests, "T.cs"), Offending);
    }

    [Fact]
    public void Test_projects_are_scanned_by_default_and_skipped_with_the_option()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-scan-excl-" + Guid.NewGuid().ToString("N"));
        try
        {
            Layout(root, out _, out _);

            var all = new ScannerEngine().Scan(root);
            var withoutTests = new ScannerEngine(excludeTestProjects: true).Scan(root);

            Assert.Equal(2, all.Findings.Count(f => f.RuleId == "OPT13-003"));
            Assert.Equal(1, withoutTests.Findings.Count(f => f.RuleId == "OPT13-003"));
            Assert.DoesNotContain(withoutTests.Findings, f => f.FilePath.Contains("App.Tests"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TotalProjectFiles_counts_only_projects_actually_scanned_not_the_ones_skipped()
    {
        // Regression guard: TotalProjectFiles previously reported DiscoverProjects().Count
        // (everything found on disk) regardless of --exclude-tests, so a solution with one app
        // project and one test project reported "Projects: 2" even though only 1 was scanned -
        // inconsistent with the C# file count on the same summary line, which DID reflect the
        // exclusion correctly.
        var root = Path.Combine(Path.GetTempPath(), "cms13-scan-excl-count-" + Guid.NewGuid().ToString("N"));
        try
        {
            Layout(root, out _, out _);

            var all = new ScannerEngine().Scan(root);
            var withoutTests = new ScannerEngine(excludeTestProjects: true).Scan(root);

            Assert.Equal(2, all.TotalProjectFiles);
            Assert.Equal(1, withoutTests.TotalProjectFiles);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
