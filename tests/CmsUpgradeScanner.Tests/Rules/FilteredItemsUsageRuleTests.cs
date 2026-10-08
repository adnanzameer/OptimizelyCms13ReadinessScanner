using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class FilteredItemsUsageRuleTests
{
    private readonly FilteredItemsUsageRule _rule = new();

    // --- Regression guard -----------------------------------------------------------------
    // sample/Web/Models/Legacy.cs accesses FilteredItems on a `dynamic` parameter. The
    // scanner's own committed report.md flagged this as a Blocker, which is not something a
    // syntax-only match on a dynamic receiver can actually prove. It must come back as an
    // unverified Warning instead.

    [Fact]
    public void Flags_dynamic_receiver_as_unverified_warning_not_blocker_syntax_only()
    {
        const string source = """
            namespace Sample;

            public class Legacy
            {
                public void Run(dynamic area)
                {
                    var items = area.FilteredItems;
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
    public void Flags_dynamic_receiver_as_unverified_warning_even_with_semantic()
    {
        // The receiver's static type genuinely is `dynamic` even once semantic info is
        // available - that is a property of the C# type system, not a tooling limitation - so
        // this must stay a Warning rather than escalate just because --semantic was supplied.
        const string source = """
            namespace Sample;

            public class Legacy
            {
                public void Run(dynamic area)
                {
                    var items = area.FilteredItems;
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: true);

        var findings = _rule.Evaluate(project).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    // --- Confirmed ContentArea: Blocker, only reachable with --semantic ---------------------

    [Fact]
    public void Flags_confirmed_ContentArea_as_blocker_with_semantic()
    {
        const string source = """
            using EPiServer.Core;

            namespace Sample;

            public class Legacy
            {
                public void Run(ContentArea area)
                {
                    var items = area.FilteredItems;
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: true);

        var findings = _rule.Evaluate(project).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Blocker, finding.Severity);
        Assert.DoesNotContain("unverified", finding.Message);
    }

    [Fact]
    public void Flags_ContentArea_as_unverified_warning_without_semantic()
    {
        // Same code as above, but run without --semantic: cannot be proven, so it must not be
        // a Blocker even though it happens to actually be a ContentArea.
        const string source = """
            using EPiServer.Core;

            namespace Sample;

            public class Legacy
            {
                public void Run(ContentArea area)
                {
                    var items = area.FilteredItems;
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: false);

        var findings = _rule.Evaluate(project).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    // --- Look-alike type: must not be flagged once semantic info disproves ContentArea -------

    [Fact]
    public void Does_not_flag_unrelated_type_with_its_own_FilteredItems_member()
    {
        const string source = """
            namespace Sample;

            public class Legacy
            {
                public void Run(NotAContentArea thing)
                {
                    var items = thing.FilteredItems;
                }
            }
            """;
        var project = ProjectContextBuilder.FromSource(source, semantic: true);

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }
}
