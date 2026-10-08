using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class ObsoleteApiUsageRuleTests
{
    private readonly ObsoleteApiUsageRule _rule = new();

    [Fact]
    public void Is_a_noop_without_semantic_compilation()
    {
        const string source = """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public void Run()
                {
                    var h = new LegacyFindHelper();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: false);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }

    [Fact]
    public void Flags_plain_Obsolete_member_as_warning()
    {
        const string source = """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public void Run()
                {
                    var h = new LegacyFindHelper();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: true);

        var findings = _rule.Evaluate(project).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("LegacyFindHelper", finding.Message);
    }

    [Fact]
    public void Flags_Obsolete_error_true_member_as_blocker()
    {
        const string source = """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public void Run()
                {
                    var h = new RemovedFindHelper();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: true);

        var findings = _rule.Evaluate(project).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Blocker, finding.Severity);
        Assert.Contains("RemovedFindHelper", finding.Message);
    }

    [Fact]
    public void Does_not_flag_non_Optimizely_obsolete_members()
    {
        const string source = """
            namespace Sample;

            public class Local
            {
                [System.Obsolete("local only")]
                public void OldMethod() { }

                public void Run()
                {
                    OldMethod();
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: true);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }
}
