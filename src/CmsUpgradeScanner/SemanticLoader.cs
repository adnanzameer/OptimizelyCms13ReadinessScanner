using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace OptimizelyCms13ReadinessScanner;

/// <summary>
/// Opt-in (--semantic) loader that compiles a project through MSBuild so rules can use the
/// semantic model. The target must be restored (obj/project.assets.json present).
/// Any failure degrades to syntax-only scanning for that project.
/// </summary>
public static class SemanticLoader
{
    public static bool TryRegisterMSBuild(out string? error)
    {
        try
        {
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
            error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // NuGet restore warnings (vulnerability advisories, NU1507 source mapping, NU1701 fallback
    // frameworks) are replayed by MSBuild as workspace "failures" although the project loaded fine.
    private static bool IsRestoreAdvisory(string message) =>
        (message.Contains("known", StringComparison.OrdinalIgnoreCase) &&
         message.Contains("vulnerability", StringComparison.OrdinalIgnoreCase)) ||
        message.Contains("package sources defined in your configuration", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("was restored using", StringComparison.OrdinalIgnoreCase);

    // NoInlining keeps MSBuild types out of the caller's JIT scope until after registration.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Compilation? Load(string projectPath, Action<string> warn)
    {
        try
        {
            // NuGetAudit off: vulnerability advisories are reported as load "failures" otherwise and are noise here.
            using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["NuGetAudit"] = "false" });
            workspace.WorkspaceFailed += (_, e) =>
            {
                var message = e.Diagnostic.Message;
                var isAdvisory = IsRestoreAdvisory(message);
                if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure && !isAdvisory)
                    warn($"{Path.GetFileName(projectPath)}: {message}");
            };
            var project = workspace.OpenProjectAsync(projectPath).GetAwaiter().GetResult();
            return project.GetCompilationAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            warn($"{Path.GetFileName(projectPath)}: semantic load failed ({ex.Message}); falling back to syntax-only.");
            return null;
        }
    }
}
