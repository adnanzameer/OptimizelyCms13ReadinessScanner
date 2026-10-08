using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OptimizelyCms13ReadinessScanner.Rules;

internal static class FindPackages
{
    public static readonly string[] Ids =
    {
        "EPiServer.Find", "EPiServer.Find.Cms", "EPiServer.Find.Framework", "EPiServer.Find.UI"
    };

    public static bool IsFindPackage(string id) => Ids.Contains(id, StringComparer.OrdinalIgnoreCase);

    public static bool IsFindNamespace(string ns) =>
        ns.Equals("EPiServer.Find", StringComparison.Ordinal) || ns.StartsWith("EPiServer.Find.", StringComparison.Ordinal);
}

// OPT13-011: any file importing an EPiServer.Find namespace.
// OPT13-002 only knows a handful of call names; the using directive is the broadest reliable
// signal that a file depends on the Find API at all (IClient injection, .Search<T>(), filters,
// UnifiedSearch, indexing conventions, ...), because the namespace goes away with the package.
// Reported once per file, at the first such directive, so a Find-heavy codebase is not flooded.
public class FindNamespaceUsageRule : IUpgradeRule
{
    public string RuleId => "OPT13-011";
    public string Title => "EPiServer.Find Namespace Imported";
    public Severity Severity => Severity.Blocker;

    public IEnumerable<Finding> Evaluate(ProjectContext project)
    {
        foreach (var file in project.SourceFiles)
        {
            if (file.SyntaxTree == null) continue;

            var imports = file.SyntaxTree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u => u.Name != null && FindPackages.IsFindNamespace(u.Name.ToString()))
                .ToList();
            if (imports.Count == 0) continue;

            var first = imports[0];
            var more = imports.Count > 1 ? $" (+{imports.Count - 1} more Find import(s) in this file)" : "";
            yield return new Finding(RuleId, Title, Severity,
                $"File imports '{first.Name}'. The Search & Navigation (Find) API is not available in CMS 13, so the code in this file that uses it must move to Optimizely Graph.{more}",
                file.FilePath, RuleHelpers.Line(first),
                "Replace the Find queries, filters and indexing conventions in this file with Optimizely Graph equivalents, then remove the using directive(s).");
        }
    }
}

// OPT13-012: a direct package reference that pulls EPiServer.Find in transitively (typically an
// add-on built on Find). Needs a restored project (obj/project.assets.json) for the dependency
// graph; without one the rule has nothing to inspect and stays silent. Direct Find references are
// OPT13-001's job, so a project that references Find itself is not reported here.
public class TransitiveFindPackageRule : IUpgradeRule
{
    public string RuleId => "OPT13-012";
    public string Title => "Package Depends On EPiServer.Find";
    public Severity Severity => Severity.Warning;

    public IEnumerable<Finding> Evaluate(ProjectContext project)
    {
        var graph = project.PackageGraph;
        if (graph == null || graph.Count == 0) yield break;

        var direct = project.PackageReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var package in direct)
        {
            if (FindPackages.IsFindPackage(package)) continue;

            var chain = ChainToFind(graph, package);
            if (chain == null) continue;

            yield return new Finding(RuleId, Title, Severity,
                $"'{package}' depends on '{chain[^1]}' (via {string.Join(" -> ", chain)}). Search & Navigation (Find) is not supported in CMS 13, so this package must drop Find, be replaced, or be removed.",
                project.ProjectFilePath, LineOf(project.ProjectFileContent, package),
                $"Check whether a CMS 13 release of {package} no longer depends on Find (see the package compatibility results), otherwise plan its replacement.");
        }
    }

    // Breadth-first so the reported chain is the shortest one; the visited set also makes
    // dependency cycles harmless.
    private static List<string>? ChainToFind(IReadOnlyDictionary<string, IReadOnlyList<string>> graph, string start)
    {
        var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [start] = null };
        var queue = new Queue<string>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!graph.TryGetValue(current, out var dependencies)) continue;

            foreach (var dependency in dependencies)
            {
                if (parent.ContainsKey(dependency)) continue;
                parent[dependency] = current;

                if (FindPackages.IsFindPackage(dependency))
                {
                    var chain = new List<string>();
                    for (string? node = dependency; node != null; node = parent[node]) chain.Add(node);
                    chain.Reverse();
                    return chain;
                }
                queue.Enqueue(dependency);
            }
        }
        return null;
    }

    private static int LineOf(string content, string packageId)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains($"Include=\"{packageId}\"", StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 1;
    }
}
