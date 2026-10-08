using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class TargetFrameworkRuleTests
{
    private readonly IUpgradeRule _rule = BuiltInRule.Load("OPT13-009");

    private static ProjectContext Project(TempCsproj p) => p.ToProjectContext();

    private static TempCsproj Csproj(string tfmElement) => new(
        $"<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    {tfmElement}\n  </PropertyGroup>\n</Project>",
        new[] { "EPiServer.CMS.Core" });

    [Fact]
    public void Flags_net8_Optimizely_project_with_correct_line()
    {
        using var p = Csproj("<TargetFramework>net8.0</TargetFramework>");

        var finding = Assert.Single(_rule.Evaluate(Project(p)));

        Assert.Equal(3, finding.LineNumber);
        Assert.Contains("net8.0", finding.Message);
    }

    [Fact]
    public void Does_not_flag_net10()
    {
        using var p = Csproj("<TargetFramework>net10.0</TargetFramework>");
        Assert.Empty(_rule.Evaluate(Project(p)));
    }

    [Fact]
    public void Does_not_flag_multi_target_that_includes_net10()
    {
        using var p = Csproj("<TargetFrameworks>net8.0;net10.0</TargetFrameworks>");
        Assert.Empty(_rule.Evaluate(Project(p)));
    }

    [Fact]
    public void Does_not_flag_non_Optimizely_project()
    {
        using var p = new TempCsproj(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup></Project>");
        Assert.Empty(_rule.Evaluate(Project(p)));
    }

    [Fact]
    public void Does_not_flag_when_framework_is_not_declared_in_the_project_file()
    {
        using var p = new TempCsproj("<Project Sdk=\"Microsoft.NET.Sdk.Web\" />", new[] { "EPiServer.CMS.Core" });
        Assert.Empty(_rule.Evaluate(Project(p)));
    }
}
