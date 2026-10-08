namespace OptimizelyCms13ReadinessScanner.Tests;

public class DirectoryBuildPropsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cms13-scan-props-" + Guid.NewGuid().ToString("N"));

    public DirectoryBuildPropsTests() => Directory.CreateDirectory(Path.Combine(_root, "src", "App"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Csproj(string body) =>
        File.WriteAllText(Path.Combine(_root, "src", "App", "App.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk.Web\">{body}</Project>");

    private void Props(string body, string relativeDir = "") =>
        File.WriteAllText(Path.Combine(_root, relativeDir, "Directory.Build.props"), $"<Project>{body}</Project>");

    private ScanSummary Scan() => new ScannerEngine().Scan(_root);

    private const string Cms = "<ItemGroup><PackageReference Include=\"EPiServer.CMS.Core\" Version=\"12.0.0\" /></ItemGroup>";

    [Fact]
    public void TreatWarningsAsErrors_set_in_Directory_Build_props_satisfies_OPT13_006()
    {
        Csproj(Cms);
        Props("<PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>");

        Assert.DoesNotContain(Scan().Findings, f => f.RuleId == "OPT13-006");
    }

    [Fact]
    public void Without_it_anywhere_OPT13_006_still_fires()
    {
        Csproj(Cms);
        Props("<PropertyGroup><Nullable>enable</Nullable></PropertyGroup>");

        Assert.Contains(Scan().Findings, f => f.RuleId == "OPT13-006");
    }

    [Fact]
    public void Target_framework_inherited_from_props_is_judged_by_OPT13_009()
    {
        Csproj(Cms);
        Props("<PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>");

        var finding = Assert.Single(Scan().Findings, f => f.RuleId == "OPT13-009");

        Assert.Contains("net8.0", finding.Message);
    }

    [Fact]
    public void A_net10_framework_in_props_clears_OPT13_009()
    {
        Csproj(Cms);
        Props("<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>");

        Assert.DoesNotContain(Scan().Findings, f => f.RuleId == "OPT13-009");
    }

    [Fact]
    public void Package_references_declared_in_props_make_the_project_an_Optimizely_project()
    {
        Csproj("");   // the csproj itself references nothing
        Props(Cms);

        // Optimizely project => the build-hygiene rule applies and (nothing sets the flag) fires.
        Assert.Contains(Scan().Findings, f => f.RuleId == "OPT13-006");
    }

    [Fact]
    public void Only_the_nearest_props_applies_unless_it_imports_its_parent()
    {
        Csproj(Cms);
        Props("<PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>");          // root
        Props("<PropertyGroup><Nullable>enable</Nullable></PropertyGroup>", "src");                           // nearest, no import

        Assert.Contains(Scan().Findings, f => f.RuleId == "OPT13-006");
    }

    [Fact]
    public void A_props_that_imports_its_parent_inherits_the_parents_settings()
    {
        Csproj(Cms);
        Props("<PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>");
        Props("<Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" />", "src");

        Assert.DoesNotContain(Scan().Findings, f => f.RuleId == "OPT13-006");
    }
}
