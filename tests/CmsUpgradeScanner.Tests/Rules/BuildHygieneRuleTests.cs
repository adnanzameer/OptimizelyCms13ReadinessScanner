using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class BuildHygieneRuleTests
{
    private readonly IUpgradeRule _rule = BuiltInRule.Load("OPT13-006");

    [Fact]
    public void Flags_Optimizely_project_missing_TreatWarningsAsErrors()
    {
        using var csproj = new TempCsproj(
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="EPiServer.CMS.Core" Version="12.0.0" />
              </ItemGroup>
            </Project>
            """,
            packageReferences: new[] { "EPiServer.CMS.Core" });

        var findings = _rule.Evaluate(csproj.ToProjectContext()).ToList();

        Assert.Single(findings);
    }

    [Fact]
    public void Does_not_flag_project_with_TreatWarningsAsErrors_enabled()
    {
        using var csproj = new TempCsproj(
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="EPiServer.CMS.Core" Version="12.0.0" />
              </ItemGroup>
            </Project>
            """,
            packageReferences: new[] { "EPiServer.CMS.Core" });

        var findings = _rule.Evaluate(csproj.ToProjectContext()).ToList();

        Assert.Empty(findings);
    }

    [Fact]
    public void Does_not_flag_non_Optimizely_project()
    {
        using var csproj = new TempCsproj(
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """,
            packageReferences: new[] { "Newtonsoft.Json" });

        var findings = _rule.Evaluate(csproj.ToProjectContext()).ToList();

        Assert.Empty(findings);
    }
}
