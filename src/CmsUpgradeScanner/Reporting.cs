using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptimizelyCms13ReadinessScanner;

public static class ConsoleReporter
{
    private const string Bar = "===============================================================================";

    public static void Render(ScanSummary s)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(Bar);
        Console.WriteLine(" Optimizely CMS 13 Upgrade Readiness Scanner (cms13-scan)");
        Console.WriteLine(Bar);
        Console.ResetColor();
        Console.WriteLine($" Target Path:    {s.TargetPath}");
        Console.WriteLine($" Projects:       {s.TotalProjectFiles}");
        Console.WriteLine($" C# Files:       {s.TotalCsharpFiles}");
        Console.WriteLine($" Total Scanned:  {s.TotalFilesScanned}");
        Console.WriteLine("-------------------------------------------------------------------------------");

        Console.Write(" Overall Score:  ");
        Console.ForegroundColor = s.ReadinessScore >= 80 ? ConsoleColor.Green
                                : s.ReadinessScore >= 50 ? ConsoleColor.Yellow
                                : ConsoleColor.Red;
        Console.WriteLine($"{s.ReadinessScore}%");
        Console.ResetColor();
        Console.WriteLine($" Summary:        {s.BlockersCount} Blockers | {s.WarningsCount} Warnings | {s.InfoCount} Info");
        if (s.BaselineApplied)
            Console.WriteLine($" New vs Baseline: {s.NewBlockersCount} Blockers | {s.NewWarningsCount} Warnings | {s.NewInfoCount} Info (gates the exit code)");
        if (s.SuppressedFindingsOrEmpty.Count > 0)
            Console.WriteLine($" Suppressed:     {s.SuppressedFindingsOrEmpty.Count} finding(s) via inline cms13-scan:disable comments");
        if (s.PackageCompatibilityChecked)
        {
            // Shown unconditionally whenever --check-packages ran, NOT only when it produced a
            // finding: a project referencing nothing but fully "Compatible" packages otherwise
            // gives no indication the check ran at all, let alone what "Compatible" actually
            // means - the single caveat most likely to mislead if it only lived in the README.
            Console.WriteLine($" Package Check:  --check-packages ran against CMS {s.PackageCompatibilityTargetMajor}. " +
                               "'Compatible' means the declared dependency range allows it, not that it was tested.");
        }
        Console.WriteLine(Bar);
        Console.WriteLine();

        if (s.Findings.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" No issues found. Solution is ready for CMS 13 upgrade evaluation.");
            Console.ResetColor();
        }
        else
        {
            foreach (var f in s.Findings)
            {
                var (color, label) = f.Severity switch
                {
                    Severity.Blocker => (ConsoleColor.Red, "[BLOCKER]"),
                    Severity.Warning => (ConsoleColor.Yellow, "[WARNING]"),
                    _ => (ConsoleColor.DarkCyan, "[INFO]   ")
                };
                Console.ForegroundColor = color;
                Console.Write($"{label} ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"{f.RuleId} - {f.RuleTitle}");
                if (f.IsBaseline == true)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(" (baseline)");
                }
                Console.WriteLine();
                Console.ResetColor();
                // Findings carry a scan-root-relative path (portable, diffable across machines);
                // reconstruct the absolute path here purely for local, click-to-open display.
                Console.WriteLine($"  File: {PathUtil.ToAbsoluteForDisplay(f.FilePath, s.TargetPath)}:{f.LineNumber}");
                Console.WriteLine($"  Note: {f.Message}");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"  Fix:  {f.SuggestedFix}");
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        if (s.SuppressedFindingsOrEmpty.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($" --- {s.SuppressedFindingsOrEmpty.Count} finding(s) suppressed via inline comment (excluded from the score and exit code) ---");
            foreach (var f in s.SuppressedFindingsOrEmpty)
                Console.WriteLine($" [SUPPRESSED] {f.RuleId} - {PathUtil.ToAbsoluteForDisplay(f.FilePath, s.TargetPath)}:{f.LineNumber} - {f.Message}");
            Console.ResetColor();
            Console.WriteLine();
        }
    }
}

public static class MarkdownReporter
{
    public static string Generate(ScanSummary s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Optimizely CMS 13 Upgrade Readiness Report");
        sb.AppendLine();
        sb.AppendLine($"**CMS 13 Readiness: {s.ReadinessScore}% — {s.BlockersCount} blockers, {s.WarningsCount} warnings, {s.InfoCount} info**");
        sb.AppendLine();
        sb.AppendLine($"- **Target Path:** `{s.TargetPath}`");
        sb.AppendLine($"- **Generated (UTC):** {s.ScannedAtUtc:yyyy-MM-dd HH:mm:ss}");
        if (s.BaselineApplied)
            sb.AppendLine($"- **Baseline comparison:** {s.NewBlockersCount} new blockers, {s.NewWarningsCount} new warnings, {s.NewInfoCount} new info (vs. a prior `--output-json` report); only new findings gate the exit code.");
        if (s.SuppressedFindingsOrEmpty.Count > 0)
            sb.AppendLine($"- **Suppressed:** {s.SuppressedFindingsOrEmpty.Count} finding(s) excluded via inline `cms13-scan:disable` comments (see appendix below).");
        if (s.PackageCompatibilityChecked)
        {
            // Unconditional, independent of whether any OPT13-010 finding exists - see the
            // matching comment in ConsoleReporter.Render for why this cannot be left to only the
            // "## Package Compatibility" section below (which is itself conditional on findings).
            sb.AppendLine($"- **Package compatibility:** Checked via `--check-packages` against CMS {s.PackageCompatibilityTargetMajor}. " +
                           "\"Compatible\" means the package's declared dependency range allows the target version, not that it was tested.");
        }
        sb.AppendLine();
        sb.AppendLine("## Executive Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Count |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Projects Scanned | {s.TotalProjectFiles} |");
        sb.AppendLine($"| C# Source Files | {s.TotalCsharpFiles} |");
        sb.AppendLine($"| Blocker Issues | {s.BlockersCount} |");
        sb.AppendLine($"| Warning Issues | {s.WarningsCount} |");
        sb.AppendLine($"| Info Items | {s.InfoCount} |");
        if (s.BaselineApplied)
        {
            sb.AppendLine($"| New Blockers (vs baseline) | {s.NewBlockersCount} |");
            sb.AppendLine($"| New Warnings (vs baseline) | {s.NewWarningsCount} |");
        }
        if (s.SuppressedFindingsOrEmpty.Count > 0)
            sb.AppendLine($"| Suppressed (inline disable) | {s.SuppressedFindingsOrEmpty.Count} |");
        sb.AppendLine();
        var main = s.Findings.Where(f => f.RuleId != PackageCompatibilityFindings.RuleId).ToList();
        var packages = s.Findings.Where(f => f.RuleId == PackageCompatibilityFindings.RuleId).ToList();
        sb.AppendLine("## Findings Breakdown");
        sb.AppendLine();

        if (main.Count == 0)
        {
            sb.AppendLine(packages.Count == 0 ? "No issues detected across scanned projects." : "No code or configuration issues detected.");
        }
        else if (s.BaselineApplied)
        {
            sb.AppendLine("| Severity | Rule ID | Title | File & Line | Message | Suggested Fix | Baseline |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var f in main)
            {
                var baselineCol = f.IsBaseline == true ? "Known" : "New";
                sb.AppendLine($"| {f.Severity} | `{f.RuleId}` | {Esc(f.RuleTitle)} | `{Esc(FileRef(f))}` | {Esc(f.Message)} | {Esc(f.SuggestedFix)} | {baselineCol} |");
            }
        }
        else
        {
            sb.AppendLine("| Severity | Rule ID | Title | File & Line | Message | Suggested Fix |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var f in main)
                sb.AppendLine($"| {f.Severity} | `{f.RuleId}` | {Esc(f.RuleTitle)} | `{Esc(FileRef(f))}` | {Esc(f.Message)} | {Esc(f.SuggestedFix)} |");
        }

        if (packages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Package Compatibility");
            sb.AppendLine();
            sb.AppendLine("Checked against the NuGet feeds from `nuget.config` (`--check-packages`). A package is treated as compatible when its declared CMS dependency range admits the target version; that is not the same as tested.");
            sb.AppendLine();
            sb.AppendLine(s.BaselineApplied
                ? "| Severity | Project | Finding | Action | Baseline |\n|---|---|---|---|---|"
                : "| Severity | Project | Finding | Action |\n|---|---|---|---|");
            foreach (var f in packages)
            {
                var project = Path.GetFileNameWithoutExtension(f.FilePath);
                var row = $"| {f.Severity} | `{Esc(project)}` | {Esc(f.Message)} | {Esc(f.SuggestedFix)} |";
                sb.AppendLine(s.BaselineApplied ? row + $" {(f.IsBaseline == true ? "Known" : "New")} |" : row);
            }
        }

        if (s.SuppressedFindingsOrEmpty.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Suppressed Findings");
            sb.AppendLine();
            sb.AppendLine("Excluded from the counts and score above via an inline `cms13-scan:disable` comment. Review periodically to confirm each suppression is still warranted.");
            sb.AppendLine();
            sb.AppendLine("| Severity | Rule ID | Title | File & Line | Message |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var f in s.SuppressedFindingsOrEmpty)
                sb.AppendLine($"| {f.Severity} | `{f.RuleId}` | {Esc(f.RuleTitle)} | `{Esc(FileRef(f))}` | {Esc(f.Message)} |");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*Report generated by `cms13-scan`*");
        return sb.ToString();
    }

    // Finding.FilePath is scan-root-relative (portable, diffable across machines/CI) - show it in
    // full rather than just the bare filename, since it also disambiguates same-named files in
    // different folders (e.g. two Legacy.cs under different project directories).
    private static string FileRef(Finding f) => $"{f.FilePath}:{f.LineNumber}";

    // Source-derived text (e.g. member access expressions) must not break or inject into the table.
    private static string Esc(string v) => v
        .Replace("\\", "\\\\").Replace("|", "\\|").Replace("`", "'")
        .Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\r", " ").Replace("\n", " ");
}

public static class JsonReporter
{
    // Internal (not private) so BaselineComparer.TryLoad can deserialize a previous report using
    // the exact same options this type serializes with.
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Generate(ScanSummary s) => JsonSerializer.Serialize(s, Options);
}
