using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests;

public class ScannerEngineSuppressionTests
{
    [Fact]
    public void Same_line_bare_disable_suppresses_all_rules_on_that_line()
    {
        using var project = new TempProjectDir();
        project.AddSource("Legacy.cs", """
            using EPiServer.Find; // cms13-scan:disable OPT13-011

            namespace Sample;

            public class Legacy
            {
                public void Run()
                {
                    var r = SearchClient.Instance.Search<object>().GetContentResult(); // cms13-scan:disable
                }
            }
            """);

        var summary = new ScannerEngine().Scan(project.DirectoryPath);

        Assert.DoesNotContain(summary.Findings, f => f.RuleId == "OPT13-002");
        Assert.Contains(summary.SuppressedFindingsOrEmpty, f => f.RuleId == "OPT13-002");
        // Suppressed findings must not count toward blockers or the score.
        Assert.Equal(0, summary.BlockersCount);
    }

    [Fact]
    public void Same_line_scoped_disable_only_suppresses_the_named_rule()
    {
        using var project = new TempProjectDir();
        project.AddSource("Legacy.cs", """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public void Run(dynamic area)
                {
                    var client = SearchClient.Instance; // cms13-scan:disable OPT13-002
                    var items = area.FilteredItems;
                }
            }
            """);

        var summary = new ScannerEngine().Scan(project.DirectoryPath);

        Assert.DoesNotContain(summary.Findings, f => f.RuleId == "OPT13-002");
        Assert.Contains(summary.Findings, f => f.RuleId == "OPT13-003");
        Assert.Contains(summary.SuppressedFindingsOrEmpty, f => f.RuleId == "OPT13-002");
    }

    [Fact]
    public void Disable_next_line_applies_only_to_the_single_following_line()
    {
        using var project = new TempProjectDir();
        project.AddSource("Legacy.cs", """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public void Run()
                {
                    // cms13-scan:disable-next-line OPT13-002
                    var r = SearchClient.Instance.Search<object>().GetContentResult();
                    var client2 = SearchClient.Instance;
                }
            }
            """);

        var summary = new ScannerEngine().Scan(project.DirectoryPath);

        Assert.DoesNotContain(summary.Findings, f => f.Message.Contains("GetContentResult"));
        Assert.Contains(summary.SuppressedFindingsOrEmpty, f => f.Message.Contains("GetContentResult"));
        // The second, unrelated SearchClient.Instance usage one line later is outside the
        // directive's scope and must still be reported.
        Assert.Contains(summary.Findings, f => f.Message.Contains("Static 'SearchClient.Instance'"));
    }

    [Fact]
    public void Free_text_reason_without_rule_ids_suppresses_everything_on_the_line()
    {
        using var project = new TempProjectDir();
        project.AddSource("Legacy.cs", """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public void Run()
                {
                    var r = SearchClient.Instance.Search<object>().GetContentResult(); // cms13-scan:disable -- tracked in JIRA-123
                }
            }
            """);

        var summary = new ScannerEngine().Scan(project.DirectoryPath);

        Assert.DoesNotContain(summary.Findings, f => f.RuleId == "OPT13-002");
        // Both the GetContentResult call and the SearchClient.Instance access live on that same
        // suppressed line, so both findings should be in the suppressed bucket.
        Assert.Equal(2, summary.SuppressedFindingsOrEmpty.Count(f => f.RuleId == "OPT13-002"));
    }

    [Fact]
    public void Suppression_in_one_file_does_not_affect_another_file()
    {
        using var project = new TempProjectDir();
        project.AddSource("A.cs", """
            using EPiServer.Find;
            namespace Sample;
            public class A
            {
                public void Run() => SearchClient.Instance.Search<object>().GetContentResult(); // cms13-scan:disable
            }
            """);
        project.AddSource("B.cs", """
            using EPiServer.Find;
            namespace Sample;
            public class B
            {
                public void Run() => SearchClient.Instance.Search<object>().GetContentResult();
            }
            """);

        var summary = new ScannerEngine().Scan(project.DirectoryPath);

        Assert.Contains(summary.Findings, f => f.FilePath.EndsWith("B.cs") && f.RuleId == "OPT13-002");
        Assert.DoesNotContain(summary.Findings, f => f.FilePath.EndsWith("A.cs") && f.RuleId == "OPT13-002");
    }

    [Fact]
    public void Suppresses_project_file_findings_via_an_xml_comment()
    {
        using var project = new TempProjectDir();
        project.WriteCsproj("""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="EPiServer.Find.Cms" Version="16.0.0" /> <!-- cms13-scan:disable OPT13-001 -->
              </ItemGroup>
            </Project>
            """);

        var summary = new ScannerEngine().Scan(project.DirectoryPath);

        Assert.DoesNotContain(summary.Findings, f => f.RuleId == "OPT13-001");
        Assert.Contains(summary.SuppressedFindingsOrEmpty, f => f.RuleId == "OPT13-001");
    }
}
