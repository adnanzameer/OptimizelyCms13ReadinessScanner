using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OptimizelyCms13ReadinessScanner.Rules;

/// <summary>
/// Evaluates a single RuleDefinition loaded from rules.json (or a --rules override/extension
/// file). Each Kind below is a small, self-contained pattern matcher; rules that genuinely need
/// semantic analysis or structural tree-walking (OPT13-002/003/005/007) stay as hand-written
/// IUpgradeRule classes in Rules.cs/ObsoleteApiUsageRule.cs rather than being forced into this
/// generic shape.
/// </summary>
public sealed class DataDrivenRule : IUpgradeRule
{
    private readonly RuleDefinition _def;

    public DataDrivenRule(RuleDefinition definition) => _def = definition;

    public string RuleId => _def.Id;
    public string Title => _def.Title;
    public Severity Severity => _def.Severity;

    public IEnumerable<Finding> Evaluate(ProjectContext project) => _def.Kind switch
    {
        "ProjectPackageReference" => EvaluateProjectPackageReference(project),
        "MemberAccessPattern" => EvaluateMemberAccessPattern(project),
        "ProjectFileMustContain" => EvaluateProjectFileMustContain(project),
        "IdentifierUsage" => EvaluateIdentifierUsage(project),
        "ProjectTargetFramework" => EvaluateProjectTargetFramework(project),
        _ => throw new InvalidOperationException(
            $"Rule '{_def.Id}' has unknown kind '{_def.Kind}'. Supported kinds: " +
            "ProjectPackageReference, MemberAccessPattern, ProjectFileMustContain, IdentifierUsage, ProjectTargetFramework.")
    };

    private IEnumerable<Finding> EvaluateProjectPackageReference(ProjectContext project)
    {
        if (string.IsNullOrEmpty(project.ProjectFileContent) || _def.Packages == null) yield break;

        var lines = project.ProjectFileContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var package in _def.Packages)
            {
                if (lines[i].Contains($"Include=\"{package}\"", StringComparison.OrdinalIgnoreCase) ||
                    lines[i].Contains($"Include='{package}'", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new Finding(RuleId, Title, Severity,
                        Format(_def.Message, ("package", package)),
                        project.ProjectFilePath, i + 1, _def.Fix ?? "");
                }
            }
        }
    }

    private IEnumerable<Finding> EvaluateMemberAccessPattern(ProjectContext project)
    {
        if (_def.MemberNamePrefix == null) yield break;

        foreach (var file in project.SourceFiles)
        {
            if (file.SyntaxTree == null) continue;
            foreach (var ma in file.SyntaxTree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var text = ma.ToString();
                var nameMatches = ma.Name.Identifier.Text.StartsWith(_def.MemberNamePrefix, StringComparison.Ordinal);
                var expressionMatches = _def.ExpressionContains == null ||
                                        text.Contains(_def.ExpressionContains, StringComparison.Ordinal);
                if (!nameMatches || !expressionMatches) continue;

                yield return new Finding(RuleId, Title, Severity,
                    Format(_def.Message, ("match", text)),
                    file.FilePath, RuleHelpers.Line(ma), _def.Fix ?? "");
            }
        }
    }

    private IEnumerable<Finding> EvaluateProjectFileMustContain(ProjectContext project)
    {
        if (string.IsNullOrEmpty(project.ProjectFileContent) || _def.RequiredText == null) yield break;
        if (_def.OnlyIfOptimizelyProject && !RuleHelpers.IsOptimizelyProject(project)) yield break;

        if (!project.ProjectFileContent.Contains(_def.RequiredText, StringComparison.OrdinalIgnoreCase) &&
            !project.InheritedBuildProps.Contains(_def.RequiredText, StringComparison.OrdinalIgnoreCase))
        {
            yield return new Finding(RuleId, Title, Severity,
                _def.Message ?? "", project.ProjectFilePath, 1, _def.Fix ?? "");
        }
    }

    private IEnumerable<Finding> EvaluateIdentifierUsage(ProjectContext project)
    {
        if (_def.Identifiers == null || _def.Identifiers.Count == 0) yield break;
        var names = new HashSet<string>(_def.Identifiers, StringComparer.Ordinal);

        foreach (var file in project.SourceFiles)
        {
            if (file.SyntaxTree == null) continue;

            // (Line, IdentifierText) per distinct occurrence, in file order. Deduped so e.g. two
            // SimpleNameSyntax nodes for the same identifier on the same line (receiver +
            // standalone reference) do not double count.
            var matches = new List<(int Line, string Text)>();
            var seen = new HashSet<(int, string)>();
            foreach (var name in file.SyntaxTree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var text = name.Identifier.Text;
                if (!names.Contains(text)) continue;
                var line = RuleHelpers.Line(name);
                if (seen.Add((line, text))) matches.Add((line, text));
            }
            if (matches.Count == 0) continue;

            if (_def.GroupPerFile)
            {
                // Collapse every match in this file into exactly one finding, at the first
                // matching line, so a widely-used identifier (e.g. SiteDefinition.Current) cannot
                // flood the report with one finding per call site. The occurrence count and the
                // full set of matched identifier names are preserved in the message rather than
                // silently dropped.
                var distinctNames = matches.Select(m => m.Text).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);
                yield return new Finding(RuleId, Title, Severity,
                    Format(_def.Message, ("match", string.Join(", ", distinctNames)), ("count", matches.Count.ToString())),
                    file.FilePath, matches[0].Line, _def.Fix ?? "");
            }
            else
            {
                foreach (var (line, text) in matches)
                {
                    yield return new Finding(RuleId, Title, Severity,
                        Format(_def.Message, ("match", text), ("count", "1")),
                        file.FilePath, line, _def.Fix ?? "");
                }
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex TargetFrameworkTag = new(
        @"<TargetFrameworks?>\s*([^<]+?)\s*</TargetFrameworks?>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly System.Text.RegularExpressions.Regex NetMoniker = new(
        @"^net(\d+)\.\d+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private IEnumerable<Finding> EvaluateProjectTargetFramework(ProjectContext project)
    {
        if (string.IsNullOrEmpty(project.ProjectFileContent) || _def.MinimumDotNetMajor is not int min) yield break;
        if (!RuleHelpers.IsOptimizelyProject(project)) yield break;

        var fromProject = TargetFrameworkTag.Match(project.ProjectFileContent);
        // Not in the project file: MSBuild takes it from Directory.Build.props, if that sets one.
        var match = fromProject.Success ? fromProject : TargetFrameworkTag.Match(project.InheritedBuildProps);
        if (!match.Success) yield break; // framework set somewhere we cannot see: cannot judge

        var monikers = match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ready = monikers.Any(m =>
        {
            var nm = NetMoniker.Match(m);
            return nm.Success && int.TryParse(nm.Groups[1].Value, out var major) && major >= min;
        });
        if (ready) yield break;

        // A match inherited from Directory.Build.props has no line in the project file itself.
        var line = fromProject.Success ? project.ProjectFileContent[..match.Index].Count(c => c == '\n') + 1 : 1;
        yield return new Finding(RuleId, Title, Severity,
            Format(_def.Message, ("framework", match.Groups[1].Value)),
            project.ProjectFilePath, line, _def.Fix ?? "");
    }

    private static string Format(string? template, params (string Key, string Value)[] substitutions)
    {
        var result = template ?? "";
        foreach (var (key, value) in substitutions)
            result = result.Replace("{" + key + "}", value);
        return result;
    }
}
