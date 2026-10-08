using OptimizelyCms13ReadinessScanner.Rules;
using OptimizelyCms13ReadinessScanner.Tests.Support;

namespace OptimizelyCms13ReadinessScanner.Tests.Rules;

public class ConfigurationAuditRuleTests
{
    private readonly ConfigurationAuditRule _rule = new();

    [Fact]
    public void Flags_legacy_Find_section_and_hardcoded_Graph_secrets()
    {
        const string appsettings = """
            {
              "EPiServer": {
                "Find": {
                  "ServiceUrl": "https://example.invalid/"
                }
              },
              "Optimizely": {
                "ContentGraph": {
                  "GatewayAddress": "https://cg.optimizely.com",
                  "AppKey": "a-real-looking-key",
                  "Secret": "YOUR_SECRET",
                  "SingleKey": "<placeholder>"
                }
              }
            }
            """;
        var project = ProjectContextBuilder.WithConfigFiles(
            ProjectContextBuilder.ConfigFile("appsettings.json", appsettings));

        var findings = _rule.Evaluate(project).ToList();

        Assert.Contains(findings, f => f.Message.Contains("Legacy 'EPiServer:Find'") && f.LineNumber == 3);
        Assert.Contains(findings, f => f.Message.Contains("'AppKey'") && f.LineNumber == 10);
        // Placeholder-looking values must not be reported as hardcoded secrets.
        Assert.DoesNotContain(findings, f => f.Message.Contains("'Secret'"));
        Assert.DoesNotContain(findings, f => f.Message.Contains("'SingleKey'"));
    }

    [Fact]
    public void Does_not_flag_clean_configuration()
    {
        const string appsettings = """
            {
              "Optimizely": {
                "ContentGraph": {
                  "GatewayAddress": "https://cg.optimizely.com",
                  "AppKey": "${GRAPH_APP_KEY}",
                  "Secret": "${GRAPH_SECRET}",
                  "SingleKey": "${GRAPH_SINGLE_KEY}"
                }
              }
            }
            """;
        var project = ProjectContextBuilder.WithConfigFiles(
            ProjectContextBuilder.ConfigFile("appsettings.json", appsettings));

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }

    // --- Regression: the "Graph section" latch never reset ---------------------------------
    // The original implementation scanned line-by-line with a boolean "inGraphSection" flag that,
    // once set true by any line mentioning Graph/ContentGraph, never reset for the rest of the
    // file. A Secret/AppKey/SingleKey key appearing later in a COMPLETELY UNRELATED section was
    // wrongly flagged as a hardcoded Graph secret. This proves the real JSON-tree-based ancestor
    // check (HasGraphAncestor) correctly scopes to keys actually nested under Graph/ContentGraph.

    [Fact]
    public void Does_not_flag_a_secret_key_outside_any_Graph_section_even_after_a_Graph_section_earlier_in_the_file()
    {
        const string appsettings = """
            {
              "Optimizely": {
                "ContentGraph": {
                  "AppKey": "${GRAPH_APP_KEY}"
                }
              },
              "SomeOtherVendor": {
                "Secret": "this-looks-like-a-real-secret-but-is-not-ours-to-flag"
              }
            }
            """;
        var project = ProjectContextBuilder.WithConfigFiles(
            ProjectContextBuilder.ConfigFile("appsettings.json", appsettings));

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }

    [Fact]
    public void Still_flags_a_second_Graph_secret_after_the_Graph_section_closes_and_reopens()
    {
        // The ancestor check must also work the OTHER way: popping out of one Graph-nested object
        // and back into a second, separate Graph-nested object still correctly re-detects it -
        // proving this isn't just "flag nothing after the first section" overcorrection.
        const string appsettings = """
            {
              "Optimizely": {
                "ContentGraph": {
                  "AppKey": "first-real-key-value"
                }
              },
              "Unrelated": {
                "Name": "not a secret"
              },
              "AnotherGraphClient": {
                "Graph": {
                  "AppKey": "second-real-key-value"
                }
              }
            }
            """;
        var project = ProjectContextBuilder.WithConfigFiles(
            ProjectContextBuilder.ConfigFile("appsettings.json", appsettings));

        var findings = _rule.Evaluate(project).Where(f => f.Message.Contains("'AppKey'")).ToList();

        Assert.Equal(2, findings.Count);
    }

    // --- Regression: a key/value pair split across lines was invisible to line-based scanning ---

    [Fact]
    public void Flags_a_hardcoded_secret_even_when_the_key_and_value_are_on_different_lines()
    {
        const string appsettings = """
            {
              "Optimizely": {
                "ContentGraph": {
                  "AppKey":
                    "a-real-looking-key-on-its-own-line"
                }
              }
            }
            """;
        var project = ProjectContextBuilder.WithConfigFiles(
            ProjectContextBuilder.ConfigFile("appsettings.json", appsettings));

        var findings = _rule.Evaluate(project).ToList();

        Assert.Contains(findings, f => f.Message.Contains("'AppKey'"));
    }

    [Fact]
    public void Malformed_json_does_not_throw_and_returns_findings_detected_before_the_parse_error()
    {
        const string appsettings = """
            {
              "EPiServer": {
                "Find": { "ServiceUrl": "https://example.invalid/" }
              },
              "Broken": [ this is not valid json
            """;
        var project = ProjectContextBuilder.WithConfigFiles(
            ProjectContextBuilder.ConfigFile("appsettings.json", appsettings));

        var findings = _rule.Evaluate(project).ToList();

        Assert.Contains(findings, f => f.Message.Contains("Legacy 'EPiServer:Find'"));
    }

    // --- web.config: now actually scanned (previously collected but silently ignored) -------

    [Fact]
    public void Flags_legacy_find_element_in_web_config()
    {
        const string webConfig = """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <episerver.find serviceUrl="https://example.invalid/" />
            </configuration>
            """;
        var project = ProjectContextBuilder.WithConfigFiles(
            ProjectContextBuilder.ConfigFile("web.config", webConfig));

        var findings = _rule.Evaluate(project).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("web.config", finding.Message);
    }

    [Fact]
    public void Does_not_flag_web_config_without_a_find_section()
    {
        const string webConfig = """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="SomeSetting" value="SomeValue" />
              </appSettings>
            </configuration>
            """;
        var project = ProjectContextBuilder.WithConfigFiles(
            ProjectContextBuilder.ConfigFile("web.config", webConfig));

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }

    [Fact]
    public void Malformed_web_config_does_not_throw()
    {
        var project = ProjectContextBuilder.WithConfigFiles(
            ProjectContextBuilder.ConfigFile("web.config", "<configuration><unterminated>"));

        var findings = _rule.Evaluate(project).ToList();

        Assert.Empty(findings);
    }
}
