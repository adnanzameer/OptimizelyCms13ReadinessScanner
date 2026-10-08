using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptimizelyCms13ReadinessScanner;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var pathArgument = new Argument<string>(
            name: "path",
            description: "Path to the solution (.sln), project (.csproj), or root directory to scan",
            getDefaultValue: () => Directory.GetCurrentDirectory());

        var markdownOption = new Option<string?>("--output-markdown", "Write a Markdown readiness report to this file");
        var jsonOption = new Option<string?>("--output-json", "Write structured JSON results to this file");
        var sarifOption = new Option<string?>("--output-sarif", "Write a SARIF 2.1.0 report to this file (GitHub code scanning / Azure DevOps PR annotations)");
        var baselineOption = new Option<string?>("--baseline", "Path to a prior --output-json report. Findings already present in it are kept in the output but tagged as baseline and excluded from the exit code; only findings new since the baseline fail the build.");
        var rulesOption = new Option<string?>("--rules", "Path to a JSON file of additional/overriding rule definitions, merged with the built-in rules.json by rule Id. See README for the schema.");
        var listRulesOption = new Option<bool>("--list-rules", "Print the effective rule manifest (built-in rules, plus any --rules override) as JSON and exit without scanning.");
        var semanticOption = new Option<bool>("--semantic", "Also compile projects via MSBuild (must be restored) to detect [Obsolete] Optimizely API usage with the semantic model");
        var excludeTestsOption = new Option<bool>("--exclude-tests", "Skip test projects (those referencing a test SDK/framework or setting IsTestProject) so their findings do not count toward the score");
        var checkApiOption = new Option<bool>("--check-api", "Download the CMS 13 assemblies (from the https feeds in nuget.config) and report Optimizely APIs the code uses that no longer exist there (OPT13-013). Needs --semantic and a restored solution; downloads packages; off by default");
        var checkPackagesOption = new Option<bool>("--check-packages", "Query the NuGet feeds from nuget.config (https only) to check whether each referenced package supports the target CMS version. Sends package ids to those feeds; off by default");
        var cmsTargetOption = new Option<int>("--cms-target-major", () => 13, "CMS major version that --check-packages tests compatibility against");
        var noFailOption = new Option<bool>("--no-fail", "Always exit 0, even when blockers are found (default exits 1 on blockers, for CI gating)");

        var root = new RootCommand("Pre-flight static analysis scanner for Optimizely CMS 12 to 13 upgrades")
        {
            pathArgument, markdownOption, jsonOption, sarifOption, baselineOption, rulesOption, listRulesOption, semanticOption, excludeTestsOption, checkPackagesOption, checkApiOption, cmsTargetOption, noFailOption
        };

        root.SetHandler(ctx =>
        {
            var path = ctx.ParseResult.GetValueForArgument(pathArgument);
            var md = ctx.ParseResult.GetValueForOption(markdownOption);
            var json = ctx.ParseResult.GetValueForOption(jsonOption);
            var sarif = ctx.ParseResult.GetValueForOption(sarifOption);
            var baselinePath = ctx.ParseResult.GetValueForOption(baselineOption);
            var rulesPath = ctx.ParseResult.GetValueForOption(rulesOption);
            var listRules = ctx.ParseResult.GetValueForOption(listRulesOption);
            var noFail = ctx.ParseResult.GetValueForOption(noFailOption);
            var semantic = ctx.ParseResult.GetValueForOption(semanticOption);
            var excludeTests = ctx.ParseResult.GetValueForOption(excludeTestsOption);
            var checkPackages = ctx.ParseResult.GetValueForOption(checkPackagesOption);
            var checkApi = ctx.ParseResult.GetValueForOption(checkApiOption);
            var cmsTarget = ctx.ParseResult.GetValueForOption(cmsTargetOption);

            void Warn(string w) => Console.Error.WriteLine($"warning: {w}");

            if (listRules)
            {
                var effective = RuleManifestLoader.LoadEffective(rulesPath, Warn);
                var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
                Console.WriteLine(JsonSerializer.Serialize(effective, options));
                ctx.ExitCode = 0;
                return;
            }

            var target = Path.GetFullPath(path);
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                Console.Error.WriteLine($"Error: target path '{target}' does not exist.");
                ctx.ExitCode = 2;
                return;
            }

            if (semantic && !SemanticLoader.TryRegisterMSBuild(out var msbuildError))
            {
                Warn($"--semantic unavailable ({msbuildError}); running syntax-only.");
                semantic = false;
            }

            PackageCompatibilityChecker? packageChecker = null;
            NuGetMetadataSource? metadataSource = null;
            if (checkApi && !semantic)
            {
                Warn("--check-api needs --semantic to resolve what the code refers to; ignoring --check-api.");
                checkApi = false;
            }

            ApiSurfaceProvider? apiProvider = null;
            if (checkPackages || checkApi)
            {
                var configRoot = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
                metadataSource = NuGetMetadataSource.CreateAsync(configRoot, Warn, CancellationToken.None).GetAwaiter().GetResult();
                packageChecker = new PackageCompatibilityChecker(metadataSource, cmsTarget);
                if (checkApi) apiProvider = new ApiSurfaceProvider(metadataSource, metadataSource, packageChecker, cmsTarget, Warn);
            }

            ScanSummary summary;
            try
            {
                // The checker also backs --check-api's version selection; OPT13-010 findings are
                // only produced when --check-packages itself was asked for.
                summary = new ScannerEngine(semantic, Warn, rulesPath, excludeTests,
                    checkPackages ? packageChecker : null, apiProvider).Scan(target);
                if (apiProvider != null) Console.Error.WriteLine("info: " + apiProvider.Summary);
            }
            finally
            {
                packageChecker?.Dispose();
                metadataSource?.Dispose();
            }

            if (!string.IsNullOrWhiteSpace(baselinePath))
            {
                if (BaselineComparer.TryLoad(baselinePath, out var baselineSummary, out var loadError))
                    summary = BaselineComparer.Apply(summary, baselineSummary!);
                else
                    Warn($"--baseline unavailable ({loadError}); running without baseline comparison.");
            }

            ConsoleReporter.Render(summary);

            if (!string.IsNullOrWhiteSpace(md))
            {
                File.WriteAllText(md, MarkdownReporter.Generate(summary));
                Console.WriteLine($"Markdown report saved to: {Path.GetFullPath(md)}");
            }
            if (!string.IsNullOrWhiteSpace(json))
            {
                File.WriteAllText(json, JsonReporter.Generate(summary));
                Console.WriteLine($"JSON report saved to: {Path.GetFullPath(json)}");
            }
            if (!string.IsNullOrWhiteSpace(sarif))
            {
                File.WriteAllText(sarif, SarifReporter.Generate(summary));
                Console.WriteLine($"SARIF report saved to: {Path.GetFullPath(sarif)}");
            }

            // NewBlockersCount equals BlockersCount when no --baseline was supplied, so this
            // gates correctly either way.
            ctx.ExitCode = (!noFail && summary.NewBlockersCount > 0) ? 1 : 0;
        });

        return await root.InvokeAsync(args);
    }
}
