namespace OptimizelyCms13ReadinessScanner;

/// <summary>Resolves overlaps between findings that describe the same problem from different sources.</summary>
public static class FindingReconciler
{
    /// <summary>
    /// OPT13-003 is a hardcoded rule for a property that --check-api (OPT13-013) verifies against the
    /// real CMS 13 assembly. Where both fire on the same line, the authoritative OPT13-013 finding
    /// is kept and OPT13-003 is dropped, so the same problem is not counted twice.
    /// </summary>
    public static void PreferApiCheckOverFilteredItemsRule(List<Finding> findings)
    {
        var apiLines = findings.Where(f => f.RuleId == "OPT13-013").Select(LineKey).ToHashSet(StringComparer.Ordinal);
        if (apiLines.Count > 0)
            findings.RemoveAll(f => f.RuleId == "OPT13-003" && apiLines.Contains(LineKey(f)));
    }

    private static string LineKey(Finding f) => Path.GetFullPath(f.FilePath).ToUpperInvariant() + ":" + f.LineNumber;
}
