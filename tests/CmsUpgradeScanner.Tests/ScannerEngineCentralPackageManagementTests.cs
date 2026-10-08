using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace OptimizelyCms13ReadinessScanner.Tests;

/// <summary>
/// Exercises ScannerEngine's Central Package Management fallback (Directory.Packages.props via
/// FindCentralPackageVersions/GetParsedCentralPackageVersions). A CPM-managed project legitimately
/// has NEITHER a resolved project.assets.json entry (not restored) NOR a Version attribute on its
/// PackageReference (CPM's whole point is that the version lives centrally instead) for most
/// packages - without this fallback, ResolvePackageVersions treated such a package as having an
/// unknown installed version, which made PackageCompatibilityChecker search for a "first
/// compatible newer release" even when the version already centrally pinned was itself already
/// compatible: a false "upgrade available" finding pointing at a version the project was already
/// on. This is the exact false positive confirmed against the real Optimizely feed with
/// EPiServer.Labs.ContentManager pinned to 1.2.0 via CPM, unrestored.
/// </summary>
public class ScannerEngineCentralPackageManagementTests
{
    // Capped below any plausible CMS target UNLESS the version resolves to exactly "9.9.9-cpm" -
    // that one version is reported Compatible. This lets a test assert "no false upgrade finding"
    // by checking for the ABSENCE of any OPT13-010 finding, which only happens if the checker was
    // actually given "9.9.9-cpm" as CurrentVersion (anything else, including "unknown", produces
    // an UpgradeAvailable/NoCompatibleRelease finding pointing at this same version).
    private sealed class CompatibleOnlyAtPinnedVersionSource : IPackageMetadataSource
    {
        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(new[] { NuGetVersion.Parse("9.9.9-cpm") });

        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(
                new[] { new PackageDependency("EPiServer.CMS.Core", VersionRange.Parse("[13.0.0, 14.0.0)")) });
    }

    private static string WriteCpmProject(string dir, string packageId)
    {
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, "App.csproj");
        // No Version attribute at all - this is what a real CPM-managed PackageReference looks
        // like; the version is supplied by the matching <PackageVersion> in Directory.Packages.props.
        File.WriteAllText(csproj, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="{packageId}" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "A.cs"), "class A {}");
        return csproj;
    }

    [Fact]
    public void A_CPM_managed_package_pinned_to_an_already_compatible_version_produces_no_finding_when_unrestored()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-scan-cpm-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteCpmProject(root, "Pkg.Cpm");
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), """
                <Project>
                  <ItemGroup>
                    <PackageVersion Include="Pkg.Cpm" Version="9.9.9-cpm" />
                  </ItemGroup>
                </Project>
                """);
            // Deliberately no obj/project.assets.json: this is the "never restored" case the fix targets.

            using var checker = new PackageCompatibilityChecker(new CompatibleOnlyAtPinnedVersionSource(), targetCmsMajor: 13);
            var summary = new ScannerEngine(packageChecker: checker).Scan(root);

            Assert.DoesNotContain(summary.Findings, f => f.RuleId == "OPT13-010");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Directory_Packages_props_found_in_an_ancestor_directory_is_used_for_a_deeply_nested_project()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-scan-cpm-nested-" + Guid.NewGuid().ToString("N"));
        try
        {
            var projectDir = Path.Combine(root, "src", "Areas", "Commerce", "Catalog");
            WriteCpmProject(projectDir, "Pkg.Cpm");
            // Directory.Packages.props lives at the repo root, several levels above the project.
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), """
                <Project>
                  <ItemGroup>
                    <PackageVersion Include="Pkg.Cpm" Version="9.9.9-cpm" />
                  </ItemGroup>
                </Project>
                """);

            using var checker = new PackageCompatibilityChecker(new CompatibleOnlyAtPinnedVersionSource(), targetCmsMajor: 13);
            var summary = new ScannerEngine(packageChecker: checker).Scan(root);

            Assert.DoesNotContain(summary.Findings, f => f.RuleId == "OPT13-010");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Without_a_Directory_Packages_props_anywhere_the_package_still_falls_back_to_unknown_version_without_throwing()
    {
        // No Directory.Packages.props at all: ResolvePackageVersions must gracefully leave the
        // package unresolved (as it did before this fix existed) rather than throwing or hanging
        // on the ancestor walk.
        var root = Path.Combine(Path.GetTempPath(), "cms13-scan-cpm-none-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteCpmProject(root, "Pkg.Cpm");

            using var checker = new PackageCompatibilityChecker(new CompatibleOnlyAtPinnedVersionSource(), targetCmsMajor: 13);
            var summary = new ScannerEngine(packageChecker: checker).Scan(root);

            // Unknown version -> the checker treats it as "nothing installed" and evaluates the
            // only known version (9.9.9-cpm, which happens to be compatible) as an available
            // upgrade - this IS a finding, just not a crash, confirming the lookup degrades
            // gracefully instead of throwing when no Directory.Packages.props exists.
            var finding = Assert.Single(summary.Findings, f => f.RuleId == "OPT13-010");
            Assert.Contains("9.9.9-cpm", finding.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_literal_Version_attribute_on_the_PackageReference_takes_precedence_over_Directory_Packages_props()
    {
        // Not a realistic CPM configuration (mixing a literal Version with central management is
        // unusual), but it pins down the intended precedence: a project-file-declared Version is
        // more specific than the centrally-declared default and must win.
        var root = Path.Combine(Path.GetTempPath(), "cms13-scan-cpm-precedence-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Pkg.Cpm" Version="9.9.9-cpm" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "A.cs"), "class A {}");
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), """
                <Project>
                  <ItemGroup>
                    <PackageVersion Include="Pkg.Cpm" Version="0.0.1-should-not-be-used" />
                  </ItemGroup>
                </Project>
                """);

            using var checker = new PackageCompatibilityChecker(new CompatibleOnlyAtPinnedVersionSource(), targetCmsMajor: 13);
            var summary = new ScannerEngine(packageChecker: checker).Scan(root);

            Assert.DoesNotContain(summary.Findings, f => f.RuleId == "OPT13-010");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_malformed_Directory_Packages_props_does_not_throw_and_leaves_the_package_unresolved()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-scan-cpm-malformed-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteCpmProject(root, "Pkg.Cpm");
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project><Unterminated>");

            using var checker = new PackageCompatibilityChecker(new CompatibleOnlyAtPinnedVersionSource(), targetCmsMajor: 13);
            var summary = new ScannerEngine(packageChecker: checker).Scan(root);

            // Degrades the same way "no file at all" does: falls back to unknown-version handling.
            var finding = Assert.Single(summary.Findings, f => f.RuleId == "OPT13-010");
            Assert.Contains("9.9.9-cpm", finding.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
