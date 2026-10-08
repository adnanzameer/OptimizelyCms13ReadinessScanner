namespace OptimizelyCms13ReadinessScanner.Tests.Support;

/// <summary>
/// Locates paths relative to the repository root from within a test run, regardless of build
/// configuration or target framework (so it keeps working if the test TFM ever changes) - found
/// by walking up from the test assembly's own output directory until CmsUpgradeScanner.sln is
/// found, rather than hardcoding a fixed number of ".." segments.
/// </summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CmsUpgradeScanner.sln")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException(
                "Could not locate the repository root (CmsUpgradeScanner.sln) above " + AppContext.BaseDirectory);
        return dir.FullName;
    });

    /// <summary>Repo-root-relative path, e.g. RepoPaths.Find("sample").</summary>
    public static string Find(params string[] segments) =>
        Path.Combine(new[] { Root.Value }.Concat(segments).ToArray());

    /// <summary>Path under this test project's own directory, e.g. RepoPaths.TestFile("Snapshots", "x.json").</summary>
    public static string TestFile(params string[] segments) =>
        Path.Combine(new[] { Root.Value, "tests", "CmsUpgradeScanner.Tests" }.Concat(segments).ToArray());
}
