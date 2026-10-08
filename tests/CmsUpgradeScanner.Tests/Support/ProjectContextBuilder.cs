namespace OptimizelyCms13ReadinessScanner.Tests.Support;

/// <summary>
/// Builds a <see cref="ProjectContext"/> around a single in-memory C# source file, either
/// syntax-only (no Compilation, mirroring a scan run without --semantic) or with a full
/// Compilation against the EPiServer.Find/EPiServer.Core stub (mirroring --semantic).
/// </summary>
internal static class ProjectContextBuilder
{
    public static ProjectContext FromSource(string source, bool semantic, string fileName = "Legacy.cs")
    {
        const string projectDir = @"C:\project";
        var filePath = Path.Combine(projectDir, fileName);

        if (!semantic)
        {
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, path: filePath);
            var fc = new FileContext(filePath, fileName, source, tree);
            return new ProjectContext(
                Path.Combine(projectDir, "Test.csproj"), projectDir,
                Array.Empty<string>(), new[] { fc }, Array.Empty<FileContext>());
        }

        var (tree2, compilation) = TestCompilation.Compile(source, filePath);
        var fileContext = new FileContext(filePath, fileName, source, tree2);
        return new ProjectContext(
            Path.Combine(projectDir, "Test.csproj"), projectDir,
            Array.Empty<string>(), new[] { fileContext }, Array.Empty<FileContext>(), compilation);
    }

    public static ProjectContext WithConfigFiles(params FileContext[] configFiles)
    {
        const string projectDir = @"C:\project";
        return new ProjectContext(
            Path.Combine(projectDir, "Test.csproj"), projectDir,
            Array.Empty<string>(), Array.Empty<FileContext>(), configFiles);
    }

    public static FileContext ConfigFile(string fileName, string content) =>
        new(Path.Combine(@"C:\project", fileName), fileName, content, SyntaxTree: null);
}
