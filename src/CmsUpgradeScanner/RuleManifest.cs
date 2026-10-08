using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptimizelyCms13ReadinessScanner;

/// <summary>
/// A single data-driven rule definition, as authored in rules.json. Only the "kind"-appropriate
/// fields need to be set for a given entry - see DataDrivenRule for how each Kind is evaluated.
///
/// This is deliberately a flat record rather than a kind-specific hierarchy: it keeps the JSON
/// schema simple to hand-author (no polymorphic $type discriminators) at the cost of every field
/// being optional and kind-dependent. DataDrivenRule.Evaluate is the single place that interprets
/// which fields apply for a given Kind.
/// </summary>
public record RuleDefinition(
    string Id,
    string Title,
    Severity Severity,
    string Kind,
    string? Message = null,
    string? Fix = null,
    // Kind = "ProjectPackageReference": flag any of these package names found as a
    // <PackageReference Include="..."> in the .csproj. {package} is substituted into Message.
    List<string>? Packages = null,
    // Kind = "MemberAccessPattern": flag a member-access expression (`x.Name`) whose member name
    // starts with MemberNamePrefix, optionally also requiring the full expression text to contain
    // ExpressionContains (e.g. to distinguish `FilterAccess.QueryDistinctAccessEdit(...)` from an
    // unrelated local method of the same name). {match} (the full expression text) is substituted
    // into Message.
    string? MemberNamePrefix = null,
    string? ExpressionContains = null,
    // Kind = "ProjectFileMustContain": flag the project file if it does NOT contain RequiredText
    // (case-insensitive substring). OnlyIfOptimizelyProject gates the check to projects that
    // reference an EPiServer.*/Optimizely.* package, so e.g. a non-CMS test utility project isn't
    // flagged for a CMS-specific build-hygiene setting.
    string? RequiredText = null,
    bool OnlyIfOptimizelyProject = false,
    // Kind = "IdentifierUsage": flag any identifier (type/member/variable name) in C# source whose
    // text equals one of these names. Syntax-only, so a same-named unrelated type also matches.
    List<string>? Identifiers = null,
    // Kind = "ProjectTargetFramework": flag Optimizely projects whose <TargetFramework(s)> does not
    // include a net{N}.0 moniker with N >= this value.
    int? MinimumDotNetMajor = null,
    // Kind = "IdentifierUsage" only: when true, every match within one source file is collapsed
    // into a SINGLE finding at that file's first matching line, instead of one finding per
    // occurrence. {match} becomes the comma-separated distinct identifier names found in the
    // file, and {count} becomes the total number of matched occurrences. Intended for rules that
    // mean "review this file against a changed model" (e.g. OPT13-008's SiteDefinition usage),
    // where a widely-used identifier can otherwise produce dozens of findings per file and bury
    // every other result in a PR annotation view. Default false preserves the original
    // one-finding-per-occurrence behaviour for rules/custom extensions that rely on it.
    bool GroupPerFile = false);

/// <summary>
/// Loads the built-in rules.json manifest (embedded in this assembly, so the tool always has it
/// regardless of how it was installed) and optionally merges in a user-supplied override/extension
/// file (--rules), by Id: a supplied definition with a matching Id replaces the built-in one, and
/// one with a new Id is added. This is how a team ships its own additional checks or tweaks a
/// built-in rule's package list/message/fix text without recompiling the scanner.
/// </summary>
public static class RuleManifestLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<RuleDefinition> LoadEmbedded()
    {
        var assembly = typeof(RuleManifestLoader).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("rules.json", StringComparison.OrdinalIgnoreCase));
        if (resourceName == null)
            throw new InvalidOperationException("Embedded rules.json manifest not found in the assembly.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<RuleDefinition> LoadFromFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// The rule set ScannerEngine should actually use for a run: the embedded built-ins, with
    /// <paramref name="overridePath"/> (if supplied) merged on top. Degrades gracefully - exactly
    /// like --semantic and --baseline do - to the built-ins alone if the override file is missing
    /// or invalid, reporting why via <paramref name="warn"/> rather than failing the whole scan.
    /// </summary>
    public static IReadOnlyList<RuleDefinition> LoadEffective(string? overridePath, Action<string> warn)
    {
        var builtins = LoadEmbedded();
        if (string.IsNullOrWhiteSpace(overridePath)) return builtins;

        try
        {
            var byId = builtins.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var o in LoadFromFile(overridePath))
            {
                var problem = Validate(o);
                if (problem != null) { warn($"--rules: skipping rule '{o.Id}': {problem}."); continue; }
                byId[o.Id] = o;
            }
            return byId.Values.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warn($"--rules unavailable ({ex.Message}); using built-in rules only.");
            return builtins;
        }
    }

    /// <summary>Null when the definition is usable; otherwise why not. Keeps a bad custom rule from crashing a scan.</summary>
    public static string? Validate(RuleDefinition r) => r.Kind switch
    {
        _ when string.IsNullOrWhiteSpace(r.Id) => "missing id",
        "ProjectPackageReference" when r.Packages is not { Count: > 0 } => "'packages' is required for kind ProjectPackageReference",
        "MemberAccessPattern" when string.IsNullOrEmpty(r.MemberNamePrefix) => "'memberNamePrefix' is required for kind MemberAccessPattern",
        "ProjectFileMustContain" when string.IsNullOrEmpty(r.RequiredText) => "'requiredText' is required for kind ProjectFileMustContain",
        "IdentifierUsage" when r.Identifiers is not { Count: > 0 } => "'identifiers' is required for kind IdentifierUsage",
        "ProjectTargetFramework" when r.MinimumDotNetMajor is null => "'minimumDotNetMajor' is required for kind ProjectTargetFramework",
        "ProjectPackageReference" or "MemberAccessPattern" or "ProjectFileMustContain" or "IdentifierUsage" or "ProjectTargetFramework" => null,
        _ => $"unknown kind '{r.Kind}' (supported: ProjectPackageReference, MemberAccessPattern, ProjectFileMustContain, IdentifierUsage, ProjectTargetFramework)"
    };

    private static IReadOnlyList<RuleDefinition> Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(json, Options)
            ?? throw new JsonException("Rule manifest deserialized to null.");
        return manifest.Rules ?? new List<RuleDefinition>();
    }

    private sealed record Manifest(List<RuleDefinition>? Rules);
}
