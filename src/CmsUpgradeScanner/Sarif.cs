using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptimizelyCms13ReadinessScanner;

/// <summary>
/// Emits SARIF 2.1.0 (https://docs.oasis-open.org/sarif/sarif/v2.1.0/sarif-v2.1.0.html), the
/// format GitHub code scanning (actions/upload-sarif), Azure DevOps SARIF extensions, and most
/// other CI annotation tooling consume to turn findings into inline PR comments instead of a
/// report someone has to separately open.
///
/// Two spec features are used rather than invented ad hoc, so downstream SARIF consumers handle
/// them natively instead of just seeing opaque extra fields:
///   - result.baselineState ("new"/"unchanged") when a --baseline was supplied.
///   - result.suppressions (kind "inSource") for findings matched by an inline
///     `cms13-scan:disable` comment - GitHub code scanning shows these as dismissed rather than
///     silently dropping them, preserving an audit trail of what was suppressed and why.
///
/// Only the subset of the spec this tool actually needs is modelled below, as private nested
/// records so the shape stays an implementation detail of this class.
/// </summary>
public static class SarifReporter
{
    private const string SchemaUri = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json";
    private const string ToolName = "cms13-scan";
    private const string SourceRootBaseId = "SRCROOT";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Generate(ScanSummary s)
    {
        var activeFindings = Order(s.Findings);
        var suppressedFindings = Order(s.SuppressedFindingsOrEmpty);

        var rules = activeFindings.Concat(suppressedFindings)
            .GroupBy(f => f.RuleId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                // Some rules (OPT13-002, OPT13-003, OPT13-007) emit different severities
                // depending on whether --semantic could confirm the finding. The catalog
                // entry's defaultConfiguration uses the worst severity seen for that rule;
                // each individual result below still carries its own accurate level.
                var worst = g.OrderBy(f => SeverityRank(f.Severity)).First();
                return new SarifReportingDescriptor(
                    Id: g.Key,
                    Name: ToIdentifier(worst.RuleTitle),
                    ShortDescription: new SarifMessage(worst.RuleTitle),
                    DefaultConfiguration: new SarifReportingConfiguration(ToSarifLevel(worst.Severity)));
            })
            .ToList();

        var targetRoot = PathUtil.NormalizeRoot(s.TargetPath);

        var results = activeFindings.Select(f => ToResult(f, targetRoot, s.BaselineApplied, suppressed: false))
            .Concat(suppressedFindings.Select(f => ToResult(f, targetRoot, baselineApplied: false, suppressed: true)))
            .ToList();

        var log = new SarifLog(
            Schema: SchemaUri,
            Version: "2.1.0",
            Runs: new List<SarifRun>
            {
                new(
                    Tool: new SarifTool(new SarifDriver(ToolName, ToolVersion(), rules)),
                    Results: results,
                    OriginalUriBaseIds: new Dictionary<string, SarifArtifactLocation>
                    {
                        [SourceRootBaseId] = new SarifArtifactLocation(new Uri(targetRoot).AbsoluteUri)
                    })
            });

        return JsonSerializer.Serialize(log, Options);
    }

    private static List<Finding> Order(IReadOnlyList<Finding> findings) => findings
        .OrderBy(f => f.RuleId, StringComparer.Ordinal)
        .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(f => f.LineNumber)
        .ToList();

    private static SarifResult ToResult(Finding f, string targetRoot, bool baselineApplied, bool suppressed) => new(
        RuleId: f.RuleId,
        Level: ToSarifLevel(f.Severity),
        Message: new SarifMessage(
            string.IsNullOrWhiteSpace(f.SuggestedFix) ? f.Message : $"{f.Message} {f.SuggestedFix}"),
        Locations: new List<SarifLocation>
        {
            new(new SarifPhysicalLocation(
                ToArtifactLocation(f.FilePath, targetRoot),
                new SarifRegion(Math.Max(1, f.LineNumber))))
        },
        BaselineState: baselineApplied ? (f.IsBaseline == true ? "unchanged" : "new") : null,
        Suppressions: suppressed ? new List<SarifSuppression> { new("inSource") } : null);

    private static string ToolVersion() =>
        typeof(SarifReporter).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    // Blocker is the worst outcome (fails CI), Info the mildest - rank them that way regardless
    // of the Severity enum's declaration order, which Max()/Min() would otherwise follow.
    private static int SeverityRank(Severity s) => s switch
    {
        Severity.Blocker => 0,
        Severity.Warning => 1,
        Severity.Info => 2,
        _ => 3
    };

    private static string ToSarifLevel(Severity s) => s switch
    {
        Severity.Blocker => "error",
        Severity.Warning => "warning",
        Severity.Info => "note",
        _ => "none"
    };

    // "Removed Property ContentArea.FilteredItems" -> "RemovedPropertyContentAreaFilteredItems".
    // SARIF reportingDescriptor.name has no required shape, but tooling expects a readable
    // identifier rather than free text with spaces/punctuation.
    private static string ToIdentifier(string title)
    {
        var chars = title.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? "Rule" : new string(chars);
    }

    private static SarifArtifactLocation ToArtifactLocation(string filePath, string targetRoot)
    {
        if (PathUtil.IsUnderRoot(filePath, targetRoot))
            return new SarifArtifactLocation(PathUtil.ToRelative(filePath, targetRoot), SourceRootBaseId);

        // Outside the scan root (should not normally happen - findings only come from files the
        // scanner itself discovered under the target) - fall back to an absolute file:// URI
        // rather than dropping the location.
        return new SarifArtifactLocation(new Uri(Path.GetFullPath(filePath)).AbsoluteUri);
    }

    // --- Minimal SARIF 2.1.0 object model (only the subset this tool emits) ----------------
    // Private and nested so this shape stays an implementation detail of SarifReporter.

    private sealed record SarifLog(
        [property: JsonPropertyName("$schema")] string Schema,
        string Version,
        List<SarifRun> Runs);

    private sealed record SarifRun(
        SarifTool Tool,
        List<SarifResult> Results,
        Dictionary<string, SarifArtifactLocation>? OriginalUriBaseIds = null);

    private sealed record SarifTool(SarifDriver Driver);

    private sealed record SarifDriver(
        string Name,
        string Version,
        List<SarifReportingDescriptor> Rules);

    private sealed record SarifReportingDescriptor(
        string Id,
        string Name,
        SarifMessage ShortDescription,
        SarifReportingConfiguration DefaultConfiguration);

    private sealed record SarifReportingConfiguration(string Level);

    private sealed record SarifResult(
        string RuleId,
        string Level,
        SarifMessage Message,
        List<SarifLocation> Locations,
        string? BaselineState = null,
        List<SarifSuppression>? Suppressions = null);

    private sealed record SarifSuppression(string Kind);

    private sealed record SarifLocation(SarifPhysicalLocation PhysicalLocation);

    private sealed record SarifPhysicalLocation(
        SarifArtifactLocation ArtifactLocation,
        SarifRegion? Region = null);

    private sealed record SarifArtifactLocation(string Uri, string? UriBaseId = null);

    private sealed record SarifRegion(int StartLine);

    private sealed record SarifMessage(string Text);
}
