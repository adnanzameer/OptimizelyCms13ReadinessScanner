using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OptimizelyCms13ReadinessScanner.Rules;

internal static class RuleHelpers
{
    public static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    // Null when there is no compilation or the file's tree is not part of it (e.g. the file is
    // outside the compiled project): rules then fall back to their syntax-only behaviour.
    public static SemanticModel? SemanticModelFor(ProjectContext project, FileContext file) =>
        project.Compilation is { } compilation && file.SyntaxTree is { } tree && compilation.ContainsSyntaxTree(tree)
            ? compilation.GetSemanticModel(tree)
            : null;

    public static bool IsOptimizelyProject(ProjectContext p) =>
        p.PackageReferences.Any(r => r.StartsWith("EPiServer.", StringComparison.OrdinalIgnoreCase) ||
                                     r.StartsWith("Optimizely.", StringComparison.OrdinalIgnoreCase));

    public static bool ImportsFind(SyntaxNode root) =>
        root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Any(u => u.Name?.ToString().StartsWith("EPiServer.Find", StringComparison.Ordinal) == true);

    // Unverified syntax-only note suffix, shared by rules that can be semantically confirmed
    // when --semantic is supplied but otherwise fall back to a pattern match.
    public const string UnverifiedSuffix = " (unverified: syntax-only match; re-run with --semantic to confirm)";
}

// OPT13-002: synchronous Find execution.
// GetContentResult is an unambiguous Find API name, so it is always a Blocker.
// GetResult is far too common a name (Task.GetAwaiter().GetResult(), ValueTask, etc.), so it is
// only ever considered in files that import EPiServer.Find, and:
//   - with --semantic, it is only reported when the symbol actually resolves to an
//     EPiServer.Find* assembly (Blocker), and silently skipped otherwise.
//   - without --semantic (syntax-only), it is reported as an unverified Warning, EXCEPT for the
//     unambiguous `.GetAwaiter().GetResult()` async-unwrap pattern, which is never a Find call
//     and is always excluded regardless of semantic availability.
public class SynchronousFindQueryRule : IUpgradeRule
{
    public string RuleId => "OPT13-002";
    public string Title => "Synchronous Find Execution Calls";
    public Severity Severity => Severity.Blocker;

    private static readonly HashSet<string> AlwaysFlagged = new(StringComparer.Ordinal) { "GetContentResult" };
    private static readonly HashSet<string> FlaggedWithFindImport = new(StringComparer.Ordinal) { "GetResult" };

    public IEnumerable<Finding> Evaluate(ProjectContext project)
    {
        foreach (var file in project.SourceFiles)
        {
            if (file.SyntaxTree == null) continue;
            var root = file.SyntaxTree.GetRoot();
            var importsFind = RuleHelpers.ImportsFind(root);
            var model = RuleHelpers.SemanticModelFor(project, file);

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;
                var name = ma.Name.Identifier.Text;
                if (!AlwaysFlagged.Contains(name) && !(importsFind && FlaggedWithFindImport.Contains(name)))
                    continue;

                // Task/ValueTask async-unwrap pattern: never a Find call, regardless of imports.
                if (name == "GetResult" && IsAwaiterUnwrap(ma.Expression))
                    continue;

                var classification = Classify(model, name, ma);
                if (classification is not (Severity severity, string note)) continue;

                yield return new Finding(RuleId, Title, severity,
                    $"Synchronous search execution call '.{name}()' detected. Graph queries in CMS 13 require async execution.{note}",
                    file.FilePath, RuleHelpers.Line(invocation),
                    "Rewrite the query using IGraphContentClient and await .GetAsContentAsync().");
            }

            foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (ma.ToString() == "SearchClient.Instance")
                {
                    yield return new Finding(RuleId, Title, Severity,
                        "Static 'SearchClient.Instance' usage detected. CMS 13 requires dependency-injected IGraphContentClient.",
                        file.FilePath, RuleHelpers.Line(ma),
                        "Inject IGraphContentClient via the constructor and remove the static SearchClient reference.");
                }
            }
        }
    }

    private static bool IsAwaiterUnwrap(ExpressionSyntax receiver) =>
        receiver is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetAwaiter" } };

    // Returns null when the call should not be reported at all (semantic model proved it is not
    // a Find symbol); otherwise the severity to report and an optional caveat note.
    private static (Severity, string)? Classify(SemanticModel? model, string name, MemberAccessExpressionSyntax ma)
    {
        if (name == "GetContentResult") return (Severity.Blocker, "");

        // name == "GetResult" from here; file is already known to import EPiServer.Find.
        if (model == null) return (Severity.Warning, RuleHelpers.UnverifiedSuffix);

        var assembly = model.GetSymbolInfo(ma).Symbol?.ContainingAssembly?.Name;
        if (assembly != null && assembly.StartsWith("EPiServer.Find", StringComparison.Ordinal))
            return (Severity.Blocker, "");

        return null; // semantic model confirms this GetResult() is unrelated to EPiServer.Find
    }
}

// OPT13-003: ContentArea.FilteredItems.
// With --semantic, only reported as a Blocker once the receiver's type is confirmed to be (or
// derive from) EPiServer.Core.ContentArea. Without --semantic, or when the receiver type cannot
// be resolved (e.g. `dynamic`, as in untyped view models), it is reported as an unverified
// Warning rather than a Blocker, since any type can expose its own "FilteredItems" member.
public class FilteredItemsUsageRule : IUpgradeRule
{
    public string RuleId => "OPT13-003";
    public string Title => "Obsolete Property ContentArea.FilteredItems";
    public Severity Severity => Severity.Blocker;

    private const string ContentAreaTypeName = "EPiServer.Core.ContentArea";

    public IEnumerable<Finding> Evaluate(ProjectContext project)
    {
        foreach (var file in project.SourceFiles)
        {
            if (file.SyntaxTree == null) continue;
            var model = RuleHelpers.SemanticModelFor(project, file);

            foreach (var ma in file.SyntaxTree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (ma.Name.Identifier.Text != "FilteredItems") continue;

                var classification = Classify(model, ma);
                if (classification is not (Severity severity, string note)) continue;

                yield return new Finding(RuleId, Title, severity,
                    $"Access to 'ContentArea.FilteredItems' detected. In CMS 13 this property is marked [Obsolete(error: true)], so any use fails to compile.{note}",
                    file.FilePath, RuleHelpers.Line(ma),
                    "Use ContentArea.Items to get all items, render with HtmlHelpers or TagHelpers, or depend on IEnumerable<IContentAreaItemsRenderingFilter> to filter by the current user (Optimizely's own guidance on the obsolete member).");
            }
        }
    }

    private static (Severity, string)? Classify(SemanticModel? model, MemberAccessExpressionSyntax ma)
    {
        if (model == null) return (Severity.Warning, RuleHelpers.UnverifiedSuffix);

        var type = model.GetTypeInfo(ma.Expression).Type;
        if (type == null || type.TypeKind is TypeKind.Dynamic or TypeKind.Error)
            return (Severity.Warning, " (unverified: receiver type could not be resolved, e.g. 'dynamic')");

        for (var t = type; t != null; t = t.BaseType)
        {
            if (t.ToDisplayString() == ContentAreaTypeName)
                return (Severity.Blocker, "");
        }

        return null; // semantic model confirms the receiver is not a ContentArea
    }
}

// OPT13-005: legacy Find config and hardcoded Graph secrets (Warning).
//
// JSON config files (appsettings*.json) are walked as a real token stream (Utf8JsonReader) with
// a path-segment stack, rather than scanned line-by-line as plain text. This fixes two bugs a
// line-based scan has no way to avoid:
//   - "Graph section" was previously a boolean latch that, once set by any line mentioning Graph/
//     ContentGraph, never reset - so a Secret/AppKey/SingleKey key appearing ANYWHERE later in the
//     file (in a completely unrelated section) was wrongly flagged as a hardcoded Graph secret.
//     Walking the real JSON tree means "is this key nested under Graph/ContentGraph" is answered
//     by the actual ancestor stack, which correctly pops back out of a section.
//   - A key/value pair split across lines (valid, if unusually formatted, JSON) could be missed
//     entirely, since the line-based scan required the colon to be on the same line as the key.
//     A real JSON reader has no such requirement.
//
// web.config is also scanned (IsConfigFile in ScannerEngine already collects it) for a legacy
// EPiServer Find configuration element, via the same XML idiom used elsewhere in the scanner.
public class ConfigurationAuditRule : IUpgradeRule
{
    public string RuleId => "OPT13-005";
    public string Title => "Legacy Find and Hardcoded Graph Configuration";
    public Severity Severity => Severity.Warning;

    private static readonly string[] SecretKeys = { "SingleKey", "AppKey", "Secret" };

    public IEnumerable<Finding> Evaluate(ProjectContext project)
    {
        foreach (var config in project.ConfigFiles)
        {
            if (config.FilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var f in EvaluateJsonConfig(config)) yield return f;
            }
            else if (Path.GetFileName(config.FilePath).Equals("web.config", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var f in EvaluateWebConfig(config)) yield return f;
            }
        }
    }

    private IEnumerable<Finding> EvaluateJsonConfig(FileContext config)
    {
        var findings = new List<Finding>();
        byte[] bytes;
        try
        {
            bytes = Encoding.UTF8.GetBytes(config.Content);
        }
        catch (EncoderFallbackException)
        {
            return findings;
        }

        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var pathStack = new List<string>();
        string? pendingProperty = null;
        var findReported = false;

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                    {
                        var name = reader.GetString()!;
                        if (!findReported && name.Equals("Find", StringComparison.OrdinalIgnoreCase) &&
                            pathStack.Count > 0 && pathStack[^1].Equals("EPiServer", StringComparison.OrdinalIgnoreCase))
                        {
                            findReported = true;
                            findings.Add(new Finding(RuleId, Title, Severity,
                                "Legacy 'EPiServer:Find' configuration section detected.",
                                config.FilePath, CountLines(bytes, (int)reader.TokenStartIndex),
                                "Replace with the Optimizely Graph configuration section (GatewayAddress, AppKey, Secret, SingleKey)."));
                        }
                        pendingProperty = name;
                        break;
                    }
                    case JsonTokenType.StartObject:
                        // Push the owning property name so nested keys can be matched against
                        // their real ancestor path; an anonymous push (empty segment) keeps depth
                        // consistent for objects with no owning property (e.g. inside an array).
                        pathStack.Add(pendingProperty ?? "");
                        pendingProperty = null;
                        break;
                    case JsonTokenType.EndObject:
                        if (pathStack.Count > 0) pathStack.RemoveAt(pathStack.Count - 1);
                        break;
                    case JsonTokenType.StartArray:
                        pendingProperty = null;
                        break;
                    case JsonTokenType.String:
                        if (pendingProperty != null)
                        {
                            if (SecretKeys.Contains(pendingProperty, StringComparer.OrdinalIgnoreCase) &&
                                HasGraphAncestor(pathStack) && LooksLikeRealSecret(reader.GetString()))
                            {
                                // The value itself is deliberately never copied into the report.
                                findings.Add(new Finding(RuleId, Title, Severity,
                                    $"Hardcoded Graph '{pendingProperty}' value detected in configuration file.",
                                    config.FilePath, CountLines(bytes, (int)reader.TokenStartIndex),
                                    "Supply Graph credentials via environment variables or a secret store (e.g. DXP app settings) so keys can be rotated without a deploy, and rotate any key committed to source control."));
                            }
                            pendingProperty = null;
                        }
                        break;
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        pendingProperty = null;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON: return whatever was found before the parse error, in the same
            // tolerant spirit as the rest of the scanner (never throws on malformed input).
        }

        return findings;
    }

    private IEnumerable<Finding> EvaluateWebConfig(FileContext config)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var stringReader = new StringReader(config.Content);
            using var reader = XmlReader.Create(stringReader, settings);
            doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            yield break;
        }

        var findElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("find", StringComparison.OrdinalIgnoreCase) ||
                                 e.Name.LocalName.Equals("episerver.find", StringComparison.OrdinalIgnoreCase));
        if (findElement == null) yield break;

        var lineInfo = (IXmlLineInfo)findElement;
        var line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;

        yield return new Finding(RuleId, Title, Severity,
            "Legacy EPiServer Find configuration element detected in web.config.",
            config.FilePath, line,
            "Remove the legacy <episerver.find>/<find> configuration section and configure Optimizely Graph instead (see the appsettings.json ContentGraph section).");
    }

    private static bool HasGraphAncestor(List<string> pathStack) =>
        pathStack.Any(p => p.Equals("Graph", StringComparison.OrdinalIgnoreCase) ||
                           p.Equals("ContentGraph", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeRealSecret(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var upper = value.ToUpperInvariant();
        return !(upper.StartsWith("YOUR_") || upper.StartsWith("<") || upper.StartsWith("${") || upper.Contains("PLACEHOLDER"));
    }

    private static int CountLines(byte[] bytes, int uptoIndex)
    {
        var count = 1;
        var limit = Math.Min(uptoIndex, bytes.Length);
        for (var i = 0; i < limit; i++)
            if (bytes[i] == (byte)'\n') count++;
        return count;
    }
}
