namespace OptimizelyCms13ReadinessScanner.Tests;

public class SuppressionIndexTests
{
    private const string File1 = @"C:\project\Legacy.cs";
    private const string File2 = @"C:\project\Other.cs";

    [Fact]
    public void Bare_marker_suppresses_every_rule_on_that_line()
    {
        var index = SuppressionIndex.Build(new[]
        {
            (File1, "var r = Foo(); // cms13-scan:disable")
        });

        Assert.True(index.IsSuppressed(File1, 1, "OPT13-001"));
        Assert.True(index.IsSuppressed(File1, 1, "OPT13-999"));
    }

    [Fact]
    public void Scoped_marker_only_suppresses_the_named_rule()
    {
        var index = SuppressionIndex.Build(new[]
        {
            (File1, "var r = Foo(); // cms13-scan:disable OPT13-002")
        });

        Assert.True(index.IsSuppressed(File1, 1, "OPT13-002"));
        Assert.False(index.IsSuppressed(File1, 1, "OPT13-003"));
    }

    [Fact]
    public void Scoped_marker_accepts_a_comma_separated_list()
    {
        var index = SuppressionIndex.Build(new[]
        {
            (File1, "var r = Foo(); // cms13-scan:disable OPT13-002,OPT13-004")
        });

        Assert.True(index.IsSuppressed(File1, 1, "OPT13-002"));
        Assert.True(index.IsSuppressed(File1, 1, "OPT13-004"));
        Assert.False(index.IsSuppressed(File1, 1, "OPT13-003"));
    }

    [Fact]
    public void Disable_next_line_targets_only_the_following_line()
    {
        var content = "// cms13-scan:disable-next-line OPT13-002\nvar r = Foo();\nvar s = Bar();";
        var index = SuppressionIndex.Build(new[] { (File1, content) });

        Assert.False(index.IsSuppressed(File1, 1, "OPT13-002")); // the directive line itself
        Assert.True(index.IsSuppressed(File1, 2, "OPT13-002"));  // the line after it
        Assert.False(index.IsSuppressed(File1, 3, "OPT13-002")); // not beyond that
    }

    [Fact]
    public void Free_text_reason_without_a_recognizable_rule_id_suppresses_everything()
    {
        var index = SuppressionIndex.Build(new[]
        {
            (File1, "var r = Foo(); // cms13-scan:disable -- false positive, see JIRA-123")
        });

        Assert.True(index.IsSuppressed(File1, 1, "OPT13-002"));
        Assert.True(index.IsSuppressed(File1, 1, "OPT13-999"));
    }

    [Fact]
    public void Works_inside_an_xml_style_comment()
    {
        var index = SuppressionIndex.Build(new[]
        {
            (File1, "<PackageReference Include=\"EPiServer.Find\" /> <!-- cms13-scan:disable OPT13-001 -->")
        });

        Assert.True(index.IsSuppressed(File1, 1, "OPT13-001"));
        Assert.False(index.IsSuppressed(File1, 1, "OPT13-002"));
    }

    [Fact]
    public void Suppression_in_one_file_does_not_leak_into_another()
    {
        var index = SuppressionIndex.Build(new[]
        {
            (File1, "var r = Foo(); // cms13-scan:disable"),
            (File2, "var r = Foo();")
        });

        Assert.True(index.IsSuppressed(File1, 1, "OPT13-002"));
        Assert.False(index.IsSuppressed(File2, 1, "OPT13-002"));
    }

    [Fact]
    public void Unrelated_lines_and_files_are_never_suppressed()
    {
        var index = SuppressionIndex.Build(new[] { (File1, "var r = Foo();\nvar s = Bar();") });

        Assert.False(index.IsSuppressed(File1, 1, "OPT13-002"));
        Assert.False(index.IsSuppressed(File1, 2, "OPT13-002"));
        Assert.False(index.IsSuppressed(@"C:\project\Nonexistent.cs", 1, "OPT13-002"));
    }

    [Fact]
    public void Merging_a_scoped_and_an_unscoped_marker_on_the_same_line_suppresses_everything()
    {
        // Two separate directives both resolving to the same target line (e.g. one same-line
        // marker plus a disable-next-line from the line above) - the more permissive one wins.
        var content = "// cms13-scan:disable-next-line\nvar r = Foo(); // cms13-scan:disable OPT13-002";
        var index = SuppressionIndex.Build(new[] { (File1, content) });

        Assert.True(index.IsSuppressed(File1, 2, "OPT13-002"));
        Assert.True(index.IsSuppressed(File1, 2, "OPT13-999")); // suppressed by the bare next-line marker
    }
}
