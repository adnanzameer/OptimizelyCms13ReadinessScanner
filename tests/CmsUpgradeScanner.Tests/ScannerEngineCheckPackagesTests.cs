using NuGet.Packaging.Core;
using NuGet.Versioning;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests;

public class ScannerEngineCheckPackagesTests
{
    private sealed class ThrowsForOnePackageSource : IPackageMetadataSource
    {
        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct)
        {
            if (id == "Flaky.Package") throw new InvalidOperationException("simulated feed failure");
            return Task.FromResult<IReadOnlyList<NuGetVersion>?>(new[] { NuGetVersion.Parse("1.0.0") });
        }

        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(Array.Empty<PackageDependency>());
    }

    [Fact]
    public void A_package_whose_check_faults_produces_an_Unknown_finding_and_a_warning_instead_of_vanishing()
    {
        // Regression guard: CheckPackages used to filter to
        // results.Where(t => t.IsCompletedSuccessfully), so a package whose check faulted or was
        // cancelled simply disappeared from the report with no finding and no warning - the
        // package looked fully vetted when its compatibility was never actually established.
        using var project = new TempProjectDir();
        project.WriteCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Flaky.Package" Version="1.0.0" />
                <PackageReference Include="Good.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        project.AddSource("A.cs", "class C { }");

        var warnings = new List<string>();
        using var checker = new PackageCompatibilityChecker(new ThrowsForOnePackageSource());

        var summary = new ScannerEngine(warn: warnings.Add, packageChecker: checker).Scan(project.DirectoryPath);

        var finding = Assert.Single(summary.Findings, f => f.RuleId == "OPT13-010");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("Flaky.Package", finding.Message);
        Assert.Contains(warnings, w => w.Contains("Flaky.Package") && w.Contains("did not complete"));
    }

    [Fact]
    public void A_project_with_no_package_references_does_not_invoke_the_checker()
    {
        using var project = new TempProjectDir();
        project.AddSource("A.cs", "class C { }");

        var calls = 0;
        using var checker = new PackageCompatibilityChecker(new CountingSource(() => calls++));

        var summary = new ScannerEngine(packageChecker: checker).Scan(project.DirectoryPath);

        Assert.DoesNotContain(summary.Findings, f => f.RuleId == "OPT13-010");
        Assert.Equal(0, calls);
    }

    private sealed class CountingSource : IPackageMetadataSource
    {
        private readonly Action _onCall;
        public CountingSource(Action onCall) => _onCall = onCall;

        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct)
        {
            _onCall();
            return Task.FromResult<IReadOnlyList<NuGetVersion>?>(null);
        }

        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(null);
    }
}
