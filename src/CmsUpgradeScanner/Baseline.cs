using System.Text.Json;

namespace OptimizelyCms13ReadinessScanner;

/// <summary>
/// Compares a freshly completed scan against a previously saved --output-json report (the
/// "baseline"), tagging each current, non-suppressed finding as IsBaseline=true (already known)
/// or false (new since the baseline was captured). CI gating then only needs to care about new
/// findings (Program.cs uses NewBlockersCount for the exit code), so a team can adopt the
/// scanner against an existing codebase without having to fix every pre-existing issue before
/// the build goes green.
///
/// Matching is by (RuleId, path relative to each run's own TargetPath, LineNumber) rather than an
/// opaque hash, so neither a differently-rooted checkout (local machine vs. a CI runner's
/// workspace) nor a change to a rule's message text - e.g. the "(unverified: ...)" suffix
/// OPT13-002/003 add or remove depending on --semantic - causes a spurious "new" finding. Line
/// numbers are still an exact match, so inserting lines earlier in a file will shift later
/// findings out of alignment with the baseline; this is a known, documented limitation.
/// </summary>
public static class BaselineComparer
{
    public static bool TryLoad(string path, out ScanSummary? baseline, out string? error)
    {
        try
        {
            var json = File.ReadAllText(path);
            baseline = JsonSerializer.Deserialize<ScanSummary>(json, JsonReporter.Options);
            if (baseline == null)
            {
                error = "file deserialized to null";
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            baseline = null;
            error = ex.Message;
            return false;
        }
    }

    public static ScanSummary Apply(ScanSummary current, ScanSummary baseline)
    {
        var baselineRoot = PathUtil.NormalizeRoot(baseline.TargetPath);
        var currentRoot = PathUtil.NormalizeRoot(current.TargetPath);

        // Count-based matching (not a set): if the baseline had two findings with an identical
        // key - e.g. OPT13-002 legitimately fires twice on the same line, as sample/Legacy.cs:10
        // does - and the current run still has two, both are "known"; a third would be "new".
        var remaining = baseline.Findings
            .GroupBy(f => KeyOf(f, baselineRoot))
            .ToDictionary(g => g.Key, g => g.Count());

        var updated = new List<Finding>(current.Findings.Count);
        foreach (var f in current.Findings)
        {
            var key = KeyOf(f, currentRoot);
            if (remaining.TryGetValue(key, out var count) && count > 0)
            {
                remaining[key] = count - 1;
                updated.Add(f with { IsBaseline = true });
            }
            else
            {
                updated.Add(f with { IsBaseline = false });
            }
        }

        return current with
        {
            Findings = updated,
            BaselineApplied = true,
            NewBlockersCount = updated.Count(f => f.Severity == Severity.Blocker && f.IsBaseline != true),
            NewWarningsCount = updated.Count(f => f.Severity == Severity.Warning && f.IsBaseline != true),
            NewInfoCount = updated.Count(f => f.Severity == Severity.Info && f.IsBaseline != true)
        };
    }

    private static (string RuleId, string RelativePath, int LineNumber) KeyOf(Finding f, string root) =>
        (f.RuleId, PathUtil.ToRelative(f.FilePath, root), f.LineNumber);
}
