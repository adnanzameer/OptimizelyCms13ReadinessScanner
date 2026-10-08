namespace OptimizelyCms13ReadinessScanner;

/// <summary>
/// Shared helpers for turning the scanner's Finding.FilePath values into paths that are portable
/// across machines and CI runs - relative to the scan's TargetPath wherever possible. Used by
/// ScannerEngine (to convert absolute paths to root-relative ones before they ever leave the
/// engine), SarifReporter (artifact locations), BaselineComparer (matching findings across two
/// separate runs), and ConsoleReporter (reconstructing an absolute path for local display).
///
/// All three methods are defensive about already-relative input (Path.IsPathRooted == false):
/// ScannerEngine.Scan emits Finding.FilePath as root-relative, so anything that re-processes a
/// Finding already produced by a scan (a loaded --baseline report, a re-serialized SARIF result)
/// must not try to re-resolve it against the filesystem or CWD.
/// </summary>
internal static class PathUtil
{
    /// <summary>
    /// The directory that relative paths are computed against: the scan target itself if it is
    /// a directory, or its containing directory if it is a .sln/.csproj file. Always ends with a
    /// trailing separator so a plain StartsWith check cannot cross a sibling directory boundary
    /// (e.g. "C:\foo" incorrectly matching "C:\foobar\file.cs").
    /// </summary>
    public static string NormalizeRoot(string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        var dir = File.Exists(full) ? Path.GetDirectoryName(full) ?? full : full;
        return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public static bool IsUnderRoot(string filePath, string root)
    {
        // Not rooted = already expressed relative to some scan root (ours or another run's);
        // by construction that is always "under" whichever root it is paired with.
        if (!Path.IsPathRooted(filePath)) return true;
        return Path.GetFullPath(filePath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Path relative to <paramref name="root"/> (as produced by <see cref="NormalizeRoot"/>),
    /// using forward slashes regardless of OS. Falls back to a forward-slash absolute path when
    /// <paramref name="filePath"/> is absolute but not under <paramref name="root"/>. Already-
    /// relative input is returned with normalized slashes, unchanged otherwise.
    /// </summary>
    public static string ToRelative(string filePath, string root)
    {
        if (!Path.IsPathRooted(filePath)) return ToForwardSlashes(filePath);

        var full = Path.GetFullPath(filePath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return ToForwardSlashes(full);

        return ToForwardSlashes(full[root.Length..]);
    }

    /// <summary>
    /// Resolves a (possibly already-relative) Finding.FilePath back to an absolute path for
    /// human-facing display (the console report). Best-effort: if filePath is already absolute,
    /// it is returned normalized as-is.
    /// </summary>
    public static string ToAbsoluteForDisplay(string filePath, string targetPath)
    {
        if (Path.IsPathRooted(filePath)) return Path.GetFullPath(filePath);
        var root = NormalizeRoot(targetPath);
        var native = filePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, native));
    }

    private static string ToForwardSlashes(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
