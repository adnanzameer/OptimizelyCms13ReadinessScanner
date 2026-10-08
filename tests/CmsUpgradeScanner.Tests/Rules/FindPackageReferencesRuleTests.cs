using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class FindPackageReferencesRuleTests
{
    private readonly IUpgradeRule _rule = BuiltInRule.Load("OPT13-001");

    [Fact]
    public void Flags_unsupported_Find_package_reference()
    {
        using var csproj = new TempCsproj("""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="EPiServer.Find.Cms" Version="16.0.0" />
              </ItemGroup>
            </Project>
            """);

        var findings = _rule.Evaluate(csproj.ToProjectContext()).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Blocker, finding.Severity);
        Assert.Contains("EPiServer.Find.Cms", finding.Message);
    }

    [Fact]
    public void Does_not_flag_unrelated_packages()
    {
        using var csproj = new TempCsproj("""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Optimizely.CMS.Core" Version="13.0.0" />
                <PackageReference Include="Optimizely.Graph.Cms.Query" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);

        var findings = _rule.Evaluate(csproj.ToProjectContext()).ToList();

        Assert.Empty(findings);
    }
}
