using Microsoft.CodeAnalysis;
using System.Text.Json.Serialization;

namespace OptimizelyCms13ReadinessScanner;

public enum Severity { Blocker, Warning, Info }

public record Finding(
    string RuleId,
    string RuleTitle,
    Severity Severity,
    string Message,
    // When produced by ScannerEngine.Scan, this is relative to the scan's TargetPath (forward
    // slashes, portable across machines/CI) - see PathUtil. Rules themselves still receive and
    // may report absolute paths (e.g. when a rule's Evaluate() is unit-tested in isolation,
    // bypassing ScannerEngine); ScannerEngine is what normalizes to relative on the way out.
    string FilePath,
    int LineNumber,
    string SuggestedFix,
    // Set by BaselineComparer.Apply: true = also present in the supplied --baseline report,
    // false = new since that baseline, null = no baseline was supplied for this run.
    bool? IsBaseline = null);

public record ScanSummary(
    string TargetPath,
    DateTime ScannedAtUtc,
    int TotalFilesScanned,
    int TotalCsharpFiles,
    int TotalProjectFiles,
    int BlockersCount,
    int WarningsCount,
    int InfoCount,
    int ReadinessScore,
    IReadOnlyList<Finding> Findings,
    // Findings that matched an inline `cms13-scan:disable` comment: excluded from the counts,
    // ReadinessScore, and the exit code, but still reported for audit visibility.
    IReadOnlyList<Finding>? SuppressedFindings = null,
    // Populated by BaselineComparer.Apply; equal to BlockersCount/WarningsCount/InfoCount when
    // no --baseline was supplied, so callers can always gate on New*Count uniformly.
    int NewBlockersCount = 0,
    int NewWarningsCount = 0,
    int NewInfoCount = 0,
    bool BaselineApplied = false,
    // True when --check-packages was supplied for this run, regardless of whether any OPT13-010
    // finding was produced. A project can reference nothing but fully "Compatible" packages, in
    // which case CheckPackages() never emits a single Finding - without this flag, the report
    // would give no indication the check ran at all, and reporters have no way to show the
    // "declared range, not tested" caveat for a run that found nothing to flag.
    bool PackageCompatibilityChecked = false,
    // The CMS major version --check-packages tested against (--cms-target-major); null when
    // PackageCompatibilityChecked is false.
    int? PackageCompatibilityTargetMajor = null)
{
    [JsonIgnore]
    public IReadOnlyList<Finding> SuppressedFindingsOrEmpty => SuppressedFindings ?? Array.Empty<Finding>();
}

public record FileContext(string FilePath, string RelativePath, string Content, SyntaxTree? SyntaxTree);

public record ProjectContext(
    string ProjectFilePath,
    string ProjectDirectory,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<FileContext> SourceFiles,
    IReadOnlyList<FileContext> ConfigFiles,
    Compilation? Compilation = null,
    // Raw text of the .csproj file itself, read once by ScannerEngine (bounded by the same
    // MaxFileBytes guard as every other file) so rules that inspect the project file - package
    // references, build-hygiene settings - work directly off this in-memory string instead of
    // each re-reading the file from disk. Empty when the project file could not be read.
    string ProjectFileContent = "",
    // Resolved NuGet version per direct package (from obj/project.assets.json when restored,
    // otherwise the csproj Version attribute); absent for packages whose version is unknown.
    IReadOnlyDictionary<string, string>? PackageVersions = null,
    // Package id -> ids of the packages it depends on, from the restored obj/project.assets.json
    // (first target framework). Null when the project has not been restored.
    IReadOnlyDictionary<string, IReadOnlyList<string>>? PackageGraph = null,
    // Concatenated text of the Directory.Build.props file(s) MSBuild would import for this project
    // (nearest first). Build settings and package references set there apply to the project.
    string InheritedBuildProps = "",
    // Every package restore resolved for the project, direct and transitive (id -> version).
    IReadOnlyDictionary<string, string>? AllPackageVersions = null);

public interface IUpgradeRule
{
    string RuleId { get; }
    string Title { get; }
    Severity Severity { get; }
    IEnumerable<Finding> Evaluate(ProjectContext project);
}
