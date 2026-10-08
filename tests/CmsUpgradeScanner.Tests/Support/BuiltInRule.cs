using OptimizelyCms13ReadinessScanner.Rules;

namespace OptimizelyCms13ReadinessScanner.Tests.Support;

/// <summary>
/// Resolves a built-in data-driven rule by Id from the real embedded rules.json manifest, for
/// tests that exercise a data-driven rule's behavior (as opposed to tests of the manifest-loading
/// mechanism itself, which live in RuleManifestLoaderTests). Deliberately loads the SAME manifest
/// the shipped tool uses, rather than a hand-rolled test fixture, so these tests validate the
/// actual shipped rule configuration.
/// </summary>
internal static class BuiltInRule
{
    public static IUpgradeRule Load(string ruleId)
    {
        var definition = RuleManifestLoader.LoadEmbedded().Single(r => r.Id == ruleId);
        return new DataDrivenRule(definition);
    }
}
