using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OptimizelyCms13ReadinessScanner.Rules;

/// <summary>
/// OPT13-013: an Optimizely/EPiServer type or member the code uses that is gone from, or marked
/// [Obsolete] in, the CMS 13 assemblies. Needs --semantic (to resolve what each name refers to) and
/// --check-api (to download the CMS 13 assemblies it compares against).
///
/// Three outcomes: the symbol no longer exists; it exists but is [Obsolete(error: true)] (using it
/// does not compile - how CMS 13 retires most APIs); or it is [Obsolete] without error (a warning).
///
/// Name-based: a member counts as present if its name exists on the type or anything it inherits,
/// so a signature change that keeps the name is not detected. A "gone" finding is a Blocker only
/// when the symbol's own assembly is among the CMS 13 assemblies that were indexed; otherwise the
/// type may simply live in a package that was not checked, and it is reported as a Warning to verify.
/// </summary>
public class CmsApiRemovalRule : IUpgradeRule
{
    private readonly ApiSurfaceProvider _provider;

    public CmsApiRemovalRule(ApiSurfaceProvider provider) => _provider = provider;

    public string RuleId => "OPT13-013";
    public string Title => "API Removed Or Obsolete In CMS 13";
    public Severity Severity => Severity.Blocker;

    public IEnumerable<Finding> Evaluate(ProjectContext project)
    {
        var compilation = project.Compilation;
        if (compilation == null) yield break;

        var surface = _provider.EnsureFor(project);
        if (surface == null || surface.TypeCount == 0) yield break;

        var target = _provider.TargetMajor;
        var seen = new HashSet<(string, int, string)>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (string.IsNullOrEmpty(tree.FilePath) || !IsUnder(tree.FilePath, project.ProjectDirectory)) continue;

            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name).Symbol;
                if (symbol == null || !TryDescribe(symbol, out var key)) continue;

                var assembly = key.Assembly;
                var typeMissing = !surface.HasType(key.TypeName);

                var memberMissing = false;
                ObsoleteInfo? obsolete = null;
                if (!typeMissing)
                {
                    if (key.MemberName == null) obsolete = surface.TypeObsolete(key.TypeName);
                    else
                    {
                        var inspection = surface.Inspect(key.TypeName, key.MemberName);
                        memberMissing = inspection.Lookup == MemberLookup.NotFound;
                        obsolete = inspection.Obsolete;
                    }
                }
                if (!typeMissing && !memberMissing && obsolete == null) continue;

                var line = RuleHelpers.Line(name);
                var subject = typeMissing || key.MemberName == null ? key.TypeName : key.TypeName + "." + key.MemberName;
                if (!seen.Add((tree.FilePath, line, subject))) continue;

                // CMS 13 mostly retires APIs by marking them [Obsolete(error: true)] instead of deleting
                // them; the attribute's message is Optimizely's own guidance on the replacement.
                if (obsolete != null && !memberMissing)
                {
                    var note = string.IsNullOrWhiteSpace(obsolete.Message) ? null : obsolete.Message.Trim();
                    yield return new Finding(RuleId, Title,
                        obsolete.IsError ? Severity.Blocker : Severity.Warning,
                        $"'{subject}' (from {assembly}) is marked [Obsolete{(obsolete.IsError ? ", error" : "")}] in CMS {target}" +
                        (obsolete.IsError ? ", so using it does not compile." : "; it still compiles but is slated for removal."),
                        tree.FilePath, line,
                        note ?? $"Check the CMS {target} breaking-changes documentation for the replacement.");
                    continue;
                }

                var confirmed = surface.AssemblyNames.Contains(assembly);
                // An assembly that was not indexed is only meaningful for the CMS platform itself (if its
                // CMS 13 packages were indexed and it is absent, it was renamed or removed). For any other
                // package we simply have no CMS 13 build to compare with, which OPT13-010 already reports.
                if (!confirmed && !IsPlatformAssembly(assembly)) continue;
                var what = typeMissing
                    ? $"Type '{key.TypeName}' (from {assembly}) was not found in the CMS {target} assemblies that were checked."
                    : $"Member '{key.MemberName}' of '{key.TypeName}' (from {assembly}) was not found in CMS {target}.";
                var caveat = confirmed
                    ? ""
                    : $" '{assembly}' itself is not among the {surface.AssemblyNames.Count} CMS {target} assemblies indexed, so it may have moved to a package that was not checked.";

                yield return new Finding(RuleId, Title,
                    confirmed ? Severity.Blocker : Severity.Warning,
                    what + caveat,
                    tree.FilePath, line,
                    $"Look up the replacement in the CMS {target} breaking-changes documentation; this code will not compile against CMS {target} as is.");
            }
        }
    }

    private readonly record struct ApiKey(string Assembly, string TypeName, string? MemberName);

    // Maps a resolved symbol to the (assembly, metadata type name, member name) it would have in
    // metadata. Null result = not something we compare (not an Optimizely API, local, synthesized).
    private static bool TryDescribe(ISymbol symbol, out ApiKey key)
    {
        key = default;
        symbol = symbol.OriginalDefinition;

        var assembly = symbol.ContainingAssembly?.Name;
        if (assembly == null ||
            !(assembly.StartsWith("EPiServer", StringComparison.Ordinal) || assembly.StartsWith("Optimizely", StringComparison.Ordinal)) ||
            assembly.StartsWith("EPiServer.Find", StringComparison.Ordinal) || // Find is OPT13-001/011/012's concern
            symbol.IsImplicitlyDeclared)
            return false;

        switch (symbol)
        {
            case INamedTypeSymbol type when type.TypeKind != TypeKind.Error:
                key = new ApiKey(assembly, MetadataName(type), null);
                return true;

            case IMethodSymbol method:
                // Accessors are reported through the property/event they belong to.
                if (method.AssociatedSymbol != null) return TryDescribe(method.AssociatedSymbol, out key);
                if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion))
                    return false;
                return Member(assembly, method.ContainingType, method.MethodKind == MethodKind.Constructor ? ".ctor" : method.MetadataName, out key);

            case IPropertySymbol property:
                return Member(assembly, property.ContainingType, property.MetadataName, out key);
            case IFieldSymbol field:
                return Member(assembly, field.ContainingType, field.Name, out key);
            case IEventSymbol evt:
                return Member(assembly, evt.ContainingType, evt.Name, out key);

            default:
                return false;
        }
    }

    private static bool Member(string assembly, INamedTypeSymbol? containing, string member, out ApiKey key)
    {
        key = default;
        if (containing == null) return false;
        key = new ApiKey(assembly, MetadataName(containing), member);
        return true;
    }

    // Namespace.Outer+Inner`1 - the same shape the metadata reader produces.
    private static string MetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType != null) return MetadataName(type.ContainingType) + "+" + type.MetadataName;
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : "";
        return ns.Length == 0 ? type.MetadataName : ns + "." + type.MetadataName;
    }

    // The CMS core ships as assembly "EPiServer" (in the EPiServer.CMS.Core package), next to EPiServer.CMS.* and EPiServer.Framework*.
    private static bool IsPlatformAssembly(string assembly) =>
        assembly.Equals("EPiServer", StringComparison.Ordinal) ||
        assembly.StartsWith("EPiServer.Framework", StringComparison.Ordinal) ||
        PackageCompatibilityChecker.IsCmsPackage(assembly);

    private static bool IsUnder(string file, string dir) =>
        file.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
