namespace OptimizelyCms13ReadinessScanner.Tests;

public class RuleManifestLoaderTests
{
    [Fact]
    public void LoadEmbedded_returns_the_three_built_in_data_driven_rules()
    {
        var rules = RuleManifestLoader.LoadEmbedded();

        var ids = rules.Select(r => r.Id).ToList();
        Assert.Contains("OPT13-001", ids);
        Assert.Contains("OPT13-004", ids);
        Assert.Contains("OPT13-006", ids);

        var packageRule = rules.Single(r => r.Id == "OPT13-001");
        Assert.Equal(Severity.Blocker, packageRule.Severity);
        Assert.Equal("ProjectPackageReference", packageRule.Kind);
        Assert.Contains("EPiServer.Find", packageRule.Packages!);
    }

    [Fact]
    public void LoadEffective_with_no_override_returns_the_embedded_rules_unchanged()
    {
        var embedded = RuleManifestLoader.LoadEmbedded();
        var effective = RuleManifestLoader.LoadEffective(null, _ => { });

        Assert.Equal(embedded.Select(r => r.Id).OrderBy(x => x), effective.Select(r => r.Id).OrderBy(x => x));
    }

    [Fact]
    public void LoadEffective_with_an_override_file_replaces_a_built_in_rule_by_id()
    {
        var overridePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(overridePath, """
                {
                  "rules": [
                    {
                      "id": "OPT13-001",
                      "title": "Custom Package Block List",
                      "severity": "Blocker",
                      "kind": "ProjectPackageReference",
                      "packages": [ "Some.Custom.Package" ],
                      "message": "Custom message for '{package}'.",
                      "fix": "Custom fix."
                    }
                  ]
                }
                """);

            var effective = RuleManifestLoader.LoadEffective(overridePath, _ => { });

            var rule = effective.Single(r => r.Id == "OPT13-001");
            Assert.Equal("Custom Package Block List", rule.Title);
            Assert.Equal(new List<string> { "Some.Custom.Package" }, rule.Packages);
            // The other built-ins are untouched by an override that only targets one Id.
            Assert.Contains(effective, r => r.Id == "OPT13-004");
            Assert.Contains(effective, r => r.Id == "OPT13-006");
        }
        finally
        {
            File.Delete(overridePath);
        }
    }

    [Fact]
    public void LoadEffective_with_an_override_file_adds_a_brand_new_rule()
    {
        var overridePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(overridePath, """
                {
                  "rules": [
                    {
                      "id": "ACME-001",
                      "title": "No Direct SqlConnection Usage",
                      "severity": "Warning",
                      "kind": "MemberAccessPattern",
                      "memberNamePrefix": "Open",
                      "expressionContains": "SqlConnection",
                      "message": "Direct call to '{match}' detected.",
                      "fix": "Use the repository abstraction instead."
                    }
                  ]
                }
                """);

            var effective = RuleManifestLoader.LoadEffective(overridePath, _ => { });

            Assert.Contains(effective, r => r.Id == "ACME-001");
            // Still has every built-in alongside the new custom rule.
            Assert.Equal(RuleManifestLoader.LoadEmbedded().Count + 1, effective.Count);
        }
        finally
        {
            File.Delete(overridePath);
        }
    }

    [Fact]
    public void LoadEffective_degrades_gracefully_when_the_override_file_is_missing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".json");
        var warnings = new List<string>();

        var effective = RuleManifestLoader.LoadEffective(missingPath, warnings.Add);

        Assert.NotEmpty(warnings);
        Assert.Equal(RuleManifestLoader.LoadEmbedded().Count, effective.Count);
    }

    [Fact]
    public void LoadEffective_degrades_gracefully_when_the_override_file_is_invalid_json()
    {
        var overridePath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(overridePath, "{ not valid json ");
            var warnings = new List<string>();

            var effective = RuleManifestLoader.LoadEffective(overridePath, warnings.Add);

            Assert.NotEmpty(warnings);
            Assert.Equal(RuleManifestLoader.LoadEmbedded().Count, effective.Count);
        }
        finally
        {
            File.Delete(overridePath);
        }
    }
}
