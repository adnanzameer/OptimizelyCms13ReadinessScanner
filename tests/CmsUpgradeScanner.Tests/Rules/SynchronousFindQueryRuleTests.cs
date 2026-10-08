using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class SynchronousFindQueryRuleTests
{
    private readonly SynchronousFindQueryRule _rule = new();

    // --- Regression guard -----------------------------------------------------------------
    // This is the exact shape of sample/Web/Models/Legacy.cs, which the scanner's own
    // committed report.md previously mis-flagged as a Blocker. Task.GetAwaiter().GetResult()
    // is a plain async-unwrap, never a Find query, and must never be reported - regardless of
    // whether the file happens to import EPiServer.Find elsewhere, and regardless of
    // --semantic.

    [Fact]
    public void Does_not_flag_Task_GetAwaiter_GetResult_syntax_only()
    {
        const string source = """
            using EPiServer.Find;
            using System.Threading.Tasks;

            namespace Sample;

            public class Legacy
            {
                public void Run(Task t)
                {
                    t.GetAwaiter().GetResult();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: false);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }

    [Fact]
    public void Does_not_flag_Task_GetAwaiter_GetResult_with_semantic()
    {
        const string source = """
            using EPiServer.Find;
            using System.Threading.Tasks;

            namespace Sample;

            public class Legacy
            {
                public void Run(Task t)
                {
                    t.GetAwaiter().GetResult();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: true);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }

    // --- GetContentResult: unambiguous Find API name, always a Blocker ---------------------

    [Fact]
    public void Flags_GetContentResult_as_blocker_syntax_only()
    {
        const string source = """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public void Run()
                {
                    var r = SearchClient.Instance.Search<object>().GetContentResult();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: false);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Contains(findings, f => f.Severity == Severity.Blocker && f.Message.Contains("GetContentResult"));
    }

    // --- GetResult: common name, must stay gated behind the Find import ---------------------

    [Fact]
    public void Does_not_flag_GetResult_when_file_does_not_import_Find()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace Sample;

            public class Plain
            {
                public object Run(Task<object> t) => t.GetAwaiter().GetResult();

                public object GetResult() => new object();

                public void Call()
                {
                    var x = GetResult();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: false);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }

    [Fact]
    public void Flags_bare_GetResult_as_unverified_warning_when_syntax_only()
    {
        // Find is imported, so the heuristic applies, but without --semantic we cannot prove
        // the receiver is actually a Find query - must downgrade to Warning, not Blocker.
        const string source = """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public object Run(Sample.NotAFindClient client)
                {
                    return client.GetResult();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: false);

        var findings = _rule.Evaluate(project).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("unverified", finding.Message);
    }

    [Fact]
    public void Flags_GetResult_as_blocker_when_semantic_confirms_Find_symbol()
    {
        const string source = """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public object Run()
                {
                    return SearchClient.Instance.Search<object>().GetResult();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: true);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Contains(findings, f => f.Severity == Severity.Blocker && f.Message.Contains("GetResult")
            && !f.Message.Contains("unverified"));
    }

    [Fact]
    public void Does_not_flag_GetResult_when_semantic_proves_it_is_not_a_Find_symbol()
    {
        // Find is imported elsewhere in the file (common in real codebases with multiple
        // concerns per file), but this particular GetResult() call resolves to an unrelated
        // type. With --semantic available, the rule can and should prove this and stay silent.
        const string source = """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public object Run(NotAFindClient client)
                {
                    return client.GetResult();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: true);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }

    // --- Static SearchClient.Instance reference: unaffected by the fix, kept as a regression guard ---

    [Fact]
    public void Flags_static_SearchClient_Instance_usage()
    {
        const string source = """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public void Run()
                {
                    var client = SearchClient.Instance;
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: false);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Contains(findings, f => f.Severity == Severity.Blocker && f.Message.Contains("SearchClient.Instance"));
    }
}
