using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace OptimizelyCms13ReadinessScanner.Tests.Support;

/// <summary>
/// Builds real Roslyn compilations for rule tests, so semantic-model rules (OPT13-002's
/// GetResult() verification, OPT13-003's ContentArea check, OPT13-007) can be exercised
/// end-to-end instead of only via their syntax-only fallback path.
///
/// Two small stub assemblies stand in for the real Optimizely packages (not available on a
/// public feed):
///   - "EPiServer.Find": SearchClient/ITypeSearch/FilterAccess plus two [Obsolete] members.
///     Its assembly name deliberately matches what OPT13-002/OPT13-007 check for.
///   - "TestFixtures": EPiServer.Core.ContentArea, plus look-alike types (same member names,
///     unrelated assembly) used to prove the semantic checks key off the resolved symbol
///     rather than the member name alone.
/// </summary>
internal static class TestCompilation
{
    private const string FindStubSource = """
        namespace EPiServer.Find
        {
            public static class SearchClient
            {
                public static Client Instance => null!;
            }

            public class Client
            {
                public ITypeSearch<T> Search<T>() => null!;
            }

            public interface ITypeSearch<T>
            {
                T GetResult();
                T GetContentResult();
            }

            public static class FilterAccess
            {
                public static object? QueryDistinctAccessEdit(object items) => null;
            }

            [System.Obsolete("Use IGraphContentClient instead.")]
            public class LegacyFindHelper
            {
                public void DoWork() { }
            }

            [System.Obsolete("This throws at compile time.", error: true)]
            public class RemovedFindHelper
            {
                public void DoWork() { }
            }
        }
        """;

    private const string OtherStubSource = """
        namespace EPiServer.Core
        {
            public class ContentArea
            {
                public object? FilteredItems => null;
                public object? Items => null;
            }
        }

        namespace Sample
        {
            // Look-alike types in an assembly that is NOT EPiServer.*, used to prove the
            // semantic checks key off the resolved symbol rather than the member name alone.
            public class NotAContentArea
            {
                public object? FilteredItems => null;
            }

            public class NotAFindClient
            {
                public object GetResult() => new object();
            }
        }
        """;

    private static readonly Lazy<MetadataReference[]> BaseReferences = new(() => Net80.References.All.ToArray());
    private static readonly Lazy<MetadataReference> FindStubReference = new(() => CompileStubAssembly("EPiServer.Find", FindStubSource));
    private static readonly Lazy<MetadataReference> OtherStubReference = new(() => CompileStubAssembly("TestFixtures", OtherStubSource));

    private static MetadataReference CompileStubAssembly(string assemblyName, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            BaseReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            var diagnostics = string.Join(Environment.NewLine, result.Diagnostics);
            throw new InvalidOperationException($"Stub assembly '{assemblyName}' failed to compile:{Environment.NewLine}{diagnostics}");
        }

        stream.Position = 0;
        return MetadataReference.CreateFromStream(stream);
    }

    /// <summary>
    /// Compiles <paramref name="source"/> as the sole file of an in-memory assembly that
    /// references both stub assemblies above, returning both the parsed tree (for syntax-only
    /// rule calls) and the full compilation (for semantic rule calls). The two share the exact
    /// same SyntaxTree instance, which Roslyn's GetSemanticModel requires.
    /// </summary>
    public static (SyntaxTree Tree, Compilation Compilation) Compile(string source, string path)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        var references = BaseReferences.Value.Append(FindStubReference.Value).Append(OtherStubReference.Value).ToArray();

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return (tree, compilation);
    }
}
