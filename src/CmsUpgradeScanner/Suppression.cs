using System.Text.RegularExpressions;

namespace OptimizelyCms13ReadinessScanner;

/// <summary>
/// Inline escape hatch for individual findings. Supported markers, found anywhere on a line as a
/// raw substring - independent of the host file's actual comment syntax, so this works inside C#
/// `//` comments, XML `&lt;!-- --&gt;` comments in a .csproj, or anywhere else a human can leave
/// text on that line:
///
///   cms13-scan:disable                      - suppress every rule on this same line
///   cms13-scan:disable OPT13-003            - suppress only OPT13-003 on this same line
///   cms13-scan:disable OPT13-003,OPT13-004  - suppress a comma-separated list, same line
///   cms13-scan:disable-next-line [...]      - as above, but applies to the line that follows
///
/// Rule ids are extracted with a regex (OPT\d+-\d+) rather than by splitting on a delimiter, so
/// free-form trailing text - e.g. "cms13-scan:disable -- false positive, see JIRA-123" - degrades
/// safely to "suppress everything on this line" instead of silently matching nothing.
///
/// JSON config files (appsettings*.json) have no comment syntax, so an OPT13-005 finding there
/// cannot be suppressed this way - see README.
/// </summary>
public sealed class SuppressionIndex
{
    private const string DisableNextLineMarker = "cms13-scan:disable-next-line";
    private const string DisableMarker = "cms13-scan:disable";
    private static readonly Regex BuiltInRuleIdPattern = new(@"OPT\d+-\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Any token shaped like a rule id; only those naming an active rule count, so a trailing
    // "see JIRA-123" in the comment is ignored rather than mistaken for a rule.
    private static readonly Regex AnyRuleIdPattern = new(@"[A-Za-z][A-Za-z0-9]*-\d+", RegexOptions.Compiled);

    private HashSet<string>? _knownRuleIds;

    // Absolute file path (OrdinalIgnoreCase) -> line number -> null (suppress all rules on that
    // line) or the specific set of rule ids suppressed there.
    private readonly Dictionary<string, Dictionary<int, HashSet<string>?>> _byFile =
        new(StringComparer.OrdinalIgnoreCase);

    public static SuppressionIndex Build(IEnumerable<(string FilePath, string Content)> files, IEnumerable<string>? knownRuleIds = null)
    {
        var index = new SuppressionIndex { _knownRuleIds = knownRuleIds?.ToHashSet(StringComparer.OrdinalIgnoreCase) };
        foreach (var (filePath, content) in files)
            index.ScanFile(filePath, content);
        return index;
    }

    public bool IsSuppressed(string filePath, int lineNumber, string ruleId)
    {
        if (!_byFile.TryGetValue(filePath, out var lines)) return false;
        if (!lines.TryGetValue(lineNumber, out var ruleIds)) return false;
        return ruleIds == null || ruleIds.Contains(ruleId);
    }

    private void ScanFile(string filePath, string content)
    {
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        Dictionary<int, HashSet<string>?>? perLine = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var directive = Match(lines[i], i + 1);
            if (directive == null) continue;
            perLine ??= new Dictionary<int, HashSet<string>?>();
            Merge(perLine, directive.Value.TargetLine, directive.Value.RuleIds);
        }

        if (perLine != null) _byFile[filePath] = perLine;
    }

    private (int TargetLine, HashSet<string>? RuleIds)? Match(string line, int lineNumber)
    {
        var nextLineIdx = line.IndexOf(DisableNextLineMarker, StringComparison.OrdinalIgnoreCase);
        if (nextLineIdx >= 0)
            return (lineNumber + 1, ParseRuleIds(line[(nextLineIdx + DisableNextLineMarker.Length)..]));

        var sameLineIdx = line.IndexOf(DisableMarker, StringComparison.OrdinalIgnoreCase);
        if (sameLineIdx >= 0)
            return (lineNumber, ParseRuleIds(line[(sameLineIdx + DisableMarker.Length)..]));

        return null;
    }

    private HashSet<string>? ParseRuleIds(string textAfterMarker)
    {
        IEnumerable<string> ids;
        if (_knownRuleIds == null)
            ids = BuiltInRuleIdPattern.Matches(textAfterMarker).Select(m => m.Value);
        else
            ids = AnyRuleIdPattern.Matches(textAfterMarker).Select(m => m.Value).Where(_knownRuleIds.Contains);

        var set = ids.Select(i => i.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? null : set; // no recognised id = suppress everything on the line
    }

    private static void Merge(Dictionary<int, HashSet<string>?> perLine, int line, HashSet<string>? ruleIds)
    {
        if (!perLine.TryGetValue(line, out var existing))
        {
            perLine[line] = ruleIds;
            return;
        }
        if (existing == null || ruleIds == null) { perLine[line] = null; return; } // null = suppress all
        existing.UnionWith(ruleIds);
    }
}
