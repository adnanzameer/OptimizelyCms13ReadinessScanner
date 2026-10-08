using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace OptimizelyCms13ReadinessScanner.Tests.Support;

/// <summary>Compiles small C# sources into real assembly images, standing in for CMS 12 / CMS 13 binaries.</summary>
internal static class StubAssembly
{
    public static byte[] Emit(string assemblyName, string source)
    {
        var compilation = CSharpCompilation.Create(assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            Net80.References.All,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }

    /// <summary>User code compiled against the given stub image, as it would be against the real package.</summary>
    public static (Compilation Compilation, string FilePath) CompileUserCode(string source, byte[] referencedImage, string directory)
    {
        var path = Path.Combine(directory, "A.cs");
        var compilation = CSharpCompilation.Create("UserCode",
            new[] { CSharpSyntaxTree.ParseText(source, path: path) },
            Net80.References.All.Append(MetadataReference.CreateFromImage(referencedImage)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (compilation, path);
    }
}
