using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OptimizelyCms13ReadinessScanner.Rules;

// OPT13-007: any [Obsolete] EPiServer/Optimizely symbol referenced from source (semantic; needs --semantic).
// [Obsolete(..., error: true)] becomes a Blocker, plain [Obsolete] a Warning.
public class ObsoleteApiUsageRule : IUpgradeRule
{
    public string RuleId => "OPT13-007";
    public string Title => "Obsolete Optimizely API Usage";
    public Severity Severity => Severity.Warning;

    private const int MaxMessageLength = 300;

    public IEnumerable<Finding> Evaluate(ProjectContext project)
    {
        var compilation = project.Compilation;
        if (compilation == null) yield break;

        var seen = new HashSet<(string, int, string)>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (string.IsNullOrEmpty(tree.FilePath) || !IsUnder(tree.FilePath, project.ProjectDirectory)) continue;

            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name).Symbol;
                if (symbol == null) continue;

                var assembly = symbol.ContainingAssembly?.Name;
                if (assembly == null ||
                    !(assembly.StartsWith("EPiServer", StringComparison.Ordinal) ||
                      assembly.StartsWith("Optimizely", StringComparison.Ordinal)))
                    continue;

                var obsolete = symbol.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute");
                if (obsolete == null) continue;

                var line = RuleHelpers.Line(name);
                var display = symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
                if (!seen.Add((tree.FilePath, line, display))) continue;

                var isError = obsolete.ConstructorArguments.Length > 1 && obsolete.ConstructorArguments[1].Value is true;
                var note = obsolete.ConstructorArguments.Length > 0 ? obsolete.ConstructorArguments[0].Value as string : null;
                if (note is { Length: > MaxMessageLength }) note = note[..MaxMessageLength] + "...";

                yield return new Finding(RuleId, Title,
                    isError ? Severity.Blocker : Severity.Warning,
                    $"'{display}' (from {assembly}) is marked [Obsolete]" + (isError ? " as an error." : "."),
                    tree.FilePath, line,
                    string.IsNullOrWhiteSpace(note) ? "Check the CMS 13 breaking changes for a replacement." : note);
            }
        }
    }

    private static bool IsUnder(string file, string dir) =>
        file.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
