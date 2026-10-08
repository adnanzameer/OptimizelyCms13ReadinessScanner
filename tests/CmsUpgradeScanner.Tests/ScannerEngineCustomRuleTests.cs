using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests;

public class ScannerEngineCustomRuleTests
{
    [Fact]
    public void A_custom_rule_supplied_via_rulesManifestPath_fires_during_a_real_scan()
    {
        // End-to-end proof of the actual value proposition: a team can add its own check without
        // recompiling the scanner, by pointing --rules (rulesManifestPath here) at a JSON file.
        using var project = new TempProjectDir();
        project.AddSource("Data.cs", """
            namespace Sample;

            public class Repository
            {
                public void Run()
                {
                    var conn = new System.Data.SqlClient.SqlConnection("...");
                    conn.Open();
                }
            }
            """);

        var rulesPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(rulesPath, """
                {
                  "rules": [
                    {
                      "id": "ACME-001",
                      "title": "No Direct SqlConnection.Open",
                      "severity": "Warning",
                      "kind": "MemberAccessPattern",
                      "memberNamePrefix": "Open",
                      "expressionContains": "conn.",
                      "message": "Direct call to '{match}' detected.",
                      "fix": "Use the repository abstraction instead."
                    }
                  ]
                }
                """);

            var summary = new ScannerEngine(rulesManifestPath: rulesPath).Scan(project.DirectoryPath);

            Assert.Contains(summary.Findings, f => f.RuleId == "ACME-001" && f.Severity == Severity.Warning);
        }
        finally
        {
            File.Delete(rulesPath);
        }
    }

    [Fact]
    public void An_overridden_built_in_rule_uses_the_overridden_package_list()
    {
        using var project = new TempProjectDir();
        project.WriteCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Some.Internal.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        var rulesPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(rulesPath, """
                {
                  "rules": [
                    {
                      "id": "OPT13-001",
                      "title": "Unsupported Search & Navigation (Find) Packages",
                      "severity": "Blocker",
                      "kind": "ProjectPackageReference",
                      "packages": [ "Some.Internal.Package" ],
                      "message": "Project references unsupported package '{package}'.",
                      "fix": "Remove it."
                    }
                  ]
                }
                """);

            var summary = new ScannerEngine(rulesManifestPath: rulesPath).Scan(project.DirectoryPath);

            Assert.Contains(summary.Findings, f => f.RuleId == "OPT13-001" && f.Message.Contains("Some.Internal.Package"));
        }
        finally
        {
            File.Delete(rulesPath);
        }
    }
}
