namespace OptimizelyCms13ReadinessScanner.Tests.Support;

/// <summary>
/// The data-driven ProjectPackageReference/ProjectFileMustContain rule kinds read
/// project.ProjectFileContent (populated here, same as ScannerEngine.LoadProject does from real
/// disk) rather than touching the filesystem themselves. This still writes to a real temp
/// directory too, so ProjectFilePath is a realistic path for the Finding it produces and a real
/// file exists for anything that still legitimately expects one. Disposing deletes the temp
/// directory.
/// </summary>
internal sealed class TempCsproj : IDisposable
{
    public string DirectoryPath { get; }
    public string FilePath { get; }
    public string Content { get; }
    public IReadOnlyList<string> PackageReferences { get; }

    public TempCsproj(string content, IReadOnlyList<string>? packageReferences = null)
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "cms13-scan-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        FilePath = Path.Combine(DirectoryPath, "Test.csproj");
        File.WriteAllText(FilePath, content);
        Content = content;
        PackageReferences = packageReferences ?? Array.Empty<string>();
    }

    public ProjectContext ToProjectContext() => new(
        FilePath, DirectoryPath, PackageReferences, Array.Empty<FileContext>(), Array.Empty<FileContext>(),
        ProjectFileContent: Content);

    public void Dispose()
    {
        try { Directory.Delete(DirectoryPath, recursive: true); } catch { /* best effort */ }
    }
}
