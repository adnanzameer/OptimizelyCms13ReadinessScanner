using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace OptimizelyCms13ReadinessScanner.Tests;

/// <summary>
/// Exercises ScannerEngine's project.assets.json parse cache (GetParsedAssetsFile) across two
/// sibling projects whose ancestor-walk (FindAssetsFile) probes the SAME shared candidate path -
/// one project genuinely owns it, the other must reject it as a candidate and fall back to its
/// own declared Version. This is the realistic hazard a path-keyed cache could introduce: if the
/// cached parse result were ever attributed to the wrong project, PackageVersions would silently
/// cross-contaminate between unrelated projects in the same solution scan.
/// </summary>
public class ScannerEngineAssetsFileCachingTests
{
    // Every package is reported capped well below any plausible CMS target, with only one (very
    // old) known version - this guarantees a NoCompatibleRelease verdict rather than
    // Compatible/NotCmsDependent (which would produce no Finding to inspect), and that verdict's
    // message embeds the exact CurrentVersion string the checker was asked about - which is what
    // each project's resolved version is asserted against below.
    private sealed class AlwaysCappedSource : IPackageMetadataSource
    {
        public Task<IReadOnlyList<NuGetVersion>?> GetVersionsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>?>(new[] { NuGetVersion.Parse("0.0.1") });

        public Task<IReadOnlyList<PackageDependency>?> GetDependenciesAsync(string id, NuGetVersion v, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PackageDependency>?>(
                new[] { new PackageDependency("EPiServer.CMS.Core", VersionRange.Parse("[8.0.0, 9.0.0)")) });
    }

    [Fact]
    public void A_shared_candidate_assets_file_resolves_only_for_its_true_owner_not_a_sibling_project()
    {
        var root = Path.Combine(Path.GetTempPath(), "cms13-scan-assets-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var projectADir = Path.Combine(root, "src", "ProjectA");
            var projectBDir = Path.Combine(root, "src", "ProjectB");
            Directory.CreateDirectory(projectADir);
            Directory.CreateDirectory(projectBDir);

            var projectACsproj = Path.Combine(projectADir, "ProjectA.csproj");
            var projectBCsproj = Path.Combine(projectBDir, "ProjectB.csproj");

            File.WriteAllText(projectACsproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Pkg.Shared" Version="1.0.0-stale" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectADir, "A.cs"), "class A {}");

            File.WriteAllText(projectBCsproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Pkg.Shared" Version="2.0.0-declared" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectBDir, "B.cs"), "class B {}");

            // A single assets file at the shared ancestor "root/obj/project.assets.json" -
            // FindAssetsFile's ancestor walk will probe this exact path for BOTH ProjectA and
            // ProjectB (it is each project's "dir/obj/project.assets.json" candidate two levels
            // up), but it only legitimately belongs to ProjectA per its restore.projectPath.
            var sharedObjDir = Path.Combine(root, "obj");
            Directory.CreateDirectory(sharedObjDir);
            var projectAPathForJson = projectACsproj.Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(sharedObjDir, "project.assets.json"), $$"""
                {
                  "version": 3,
                  "libraries": {
                    "Pkg.Shared/9.9.9-resolved": { "type": "package" }
                  },
                  "project": {
                    "restore": {
                      "projectPath": "{{projectAPathForJson}}"
                    }
                  }
                }
                """);

            using var checker = new PackageCompatibilityChecker(new AlwaysCappedSource(), targetCmsMajor: 13);

            var summary = new ScannerEngine(packageChecker: checker).Scan(root);

            var findings = summary.Findings.Where(f => f.RuleId == "OPT13-010").ToList();

            // ProjectA: resolved from the shared assets file it genuinely owns.
            Assert.Contains(findings, f => f.Message.Contains("Pkg.Shared 9.9.9-resolved"));
            // ProjectB: must NOT pick up ProjectA's assets-resolved version; falls back to its
            // own declared csproj Version untouched.
            Assert.Contains(findings, f => f.Message.Contains("Pkg.Shared 2.0.0-declared"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
