namespace OptimizelyCms13ReadinessScanner.Tests.Support;

/// <summary>
/// A real temp directory containing a minimal .csproj and any number of added source files,
/// used for tests that need to exercise ScannerEngine.Scan() end-to-end. Project discovery, file
/// I/O, and the suppression index all read straight from disk, so they cannot be exercised
/// through ProjectContextBuilder's in-memory fixtures alone.
/// </summary>
internal sealed class TempProjectDir : IDisposable
{
    public string DirectoryPath { get; }
    public string CsprojPath { get; }

    public TempProjectDir()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "cms13-scan-engine-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        CsprojPath = Path.Combine(DirectoryPath, "Test.csproj");
        WriteCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
    }

    public void WriteCsproj(string content) => File.WriteAllText(CsprojPath, content);

    public string AddSource(string relativeFileName, string content)
    {
        var path = Path.Combine(DirectoryPath, relativeFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(DirectoryPath, recursive: true); } catch { /* best effort */ }
    }
}
