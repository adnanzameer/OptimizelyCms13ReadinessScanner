using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class ObsoleteFilterAccessRuleTests
{
    private readonly IUpgradeRule _rule = BuiltInRule.Load("OPT13-004");

    [Fact]
    public void Flags_direct_FilterAccess_QueryDistinctAccess_call()
    {
        const string source = """
            using EPiServer.Find;

            namespace Sample;

            public class Legacy
            {
                public void Run(object items)
                {
                    var e = FilterAccess.QueryDistinctAccessEdit(items);
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: false);

        var findings = _rule.Evaluate(project).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("FilterAccess.QueryDistinctAccessEdit", finding.Message);
    }

    [Fact]
    public void Does_not_flag_similarly_named_method_on_an_unrelated_type()
    {
        // Same method-name prefix ("QueryDistinctAccess...") but not called through a
        // "FilterAccess." receiver - the rule's own text match should leave this alone.
        const string source = """
            namespace Sample;

            public class Repository
            {
                public object QueryDistinctAccessCustom(object items) => items;

                public void Run(object items)
                {
                    var e = this.QueryDistinctAccessCustom(items);
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: false);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }
}
