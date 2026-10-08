using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using OptimizelyCms13ReadinessScanner.Rules;

namespace OptimizelyCms13ReadinessScanner;

public class ScannerEngine
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly string[] ExcludedSegments = { "bin", "obj", "node_modules", ".git", ".vs" };

    private static readonly EnumerationOptions Recurse = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Never follow symlinks/junctions: avoids cycles and escaping the scan root.
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
    };

    private readonly IReadOnlyList<IUpgradeRule> _rules;
    private readonly bool _semantic;
    private readonly bool _excludeTests;
    private readonly PackageCompatibilityChecker? _packageChecker;
    private readonly Action<string> _warn;

    // Parsed obj/project.assets.json content, keyed by absolute file path, shared across every
    // project examined during one Scan() call. A single assets file is naturally revisited more
    // than once: FindAssetsFile's ancestor walk probes the same candidate paths for every sibling
    // project in a solution that redirects BaseIntermediateOutputPath to a shared ancestor "obj"
    // directory, and - before this cache existed - the one file actually owned by a project was
    // itself parsed twice per project (once to confirm ownership, once more to extract package
    // versions). Caching the extracted result (not the JsonDocument) means nothing needs to stay
    // disposable across the cache's lifetime.
    private readonly Dictionary<string, ParsedAssetsFile?> _assetsFileCache = new(StringComparer.OrdinalIgnoreCase);

    // Parsed Directory.Packages.props content, keyed by absolute file path. A single repo
    // typically has exactly one such file at the solution root, so every project in the solution
    // shares this one cache entry rather than re-parsing it per project.
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>?> _centralPackageVersionsCache =
        new(StringComparer.OrdinalIgnoreCase);

    public ScannerEngine(bool semantic = false, Action<string>? warn = null, string? rulesManifestPath = null, bool excludeTestProjects = false,
        PackageCompatibilityChecker? packageChecker = null, ApiSurfaceProvider? apiSurface = null)
    {
        _semantic = semantic;
        _excludeTests = excludeTestProjects;
        _packageChecker = packageChecker;
        _warn = warn ?? (_ => { });

        // Rules that need the semantic model or genuine tree-structured parsing (JSON/XML config
        // walking) stay as hand-written classes; everything expressible as a simple pattern match
        // is loaded from rules.json (and optionally overridden/extended via rulesManifestPath) -
        // see RuleManifestLoader and DataDrivenRule.
        var builtInCodeRules = new IUpgradeRule[]
        {
            new SynchronousFindQueryRule(),
            new FilteredItemsUsageRule(),
            new FindNamespaceUsageRule(),
            new TransitiveFindPackageRule(),
            new ObsoleteApiUsageRule(),
            new ConfigurationAuditRule()
        };
        var dataDrivenRules = RuleManifestLoader.LoadEffective(rulesManifestPath, _warn)
            .Select(def => (IUpgradeRule)new DataDrivenRule(def));

        var apiRules = apiSurface != null ? new IUpgradeRule[] { new CmsApiRemovalRule(apiSurface) } : Array.Empty<IUpgradeRule>();

        _rules = builtInCodeRules.Concat(apiRules).Concat(dataDrivenRules).ToList();
    }

    public ScanSummary Scan(string targetPath)
    {
        var projectFiles = DiscoverProjects(targetPath);
        var projectDirs = projectFiles.Select(p => Path.GetDirectoryName(p)!).ToList();
        var allFindings = new List<Finding>();
        var suppressionSources = new List<(string FilePath, string Content)>();
        var csharpFiles = 0;
        var totalFiles = 0;
        // Counts only projects actually scanned (excludes test projects skipped via
        // --exclude-tests), so the reported "Projects:" figure matches what was really analyzed
        // instead of the total discovered on disk before filtering.
        var scannedProjectCount = 0;

        foreach (var projPath in projectFiles)
        {
            var projectDir = Path.GetDirectoryName(projPath)!;
            var nestedDirs = projectDirs.Where(d => IsStrictlyUnder(d, projectDir)).ToList();
            var ctx = LoadProject(projPath, projectDir, nestedDirs);
            if (_excludeTests && IsTestProject(ctx)) continue;
            scannedProjectCount++;
            if (_semantic) ctx = WithCompilation(ctx, SemanticLoader.Load(projPath, _warn));

            csharpFiles += ctx.SourceFiles.Count;
            totalFiles += ctx.SourceFiles.Count + ctx.ConfigFiles.Count + 1;

            foreach (var rule in _rules)
                allFindings.AddRange(rule.Evaluate(ctx));

            if (_packageChecker != null) allFindings.AddRange(CheckPackages(ctx));

            // Collect raw text for every file a finding could possibly land on, so inline
            // `cms13-scan:disable` comments are honoured regardless of which rule produced the
            // finding: source files, config files, and the project file itself (already captured
            // on ProjectContext.ProjectFileContent, since rules now read it from there too).
            suppressionSources.AddRange(ctx.SourceFiles.Select(f => (f.FilePath, f.Content)));
            suppressionSources.AddRange(ctx.ConfigFiles.Select(f => (f.FilePath, f.Content)));
            if (!string.IsNullOrEmpty(ctx.ProjectFileContent))
                suppressionSources.Add((projPath, ctx.ProjectFileContent));
        }

        FindingReconciler.PreferApiCheckOverFilteredItemsRule(allFindings);

        var suppression = SuppressionIndex.Build(suppressionSources,
            _rules.Select(r => r.RuleId).Append(PackageCompatibilityFindings.RuleId));
        var targetRoot = PathUtil.NormalizeRoot(targetPath);
        var keptFindings = new List<Finding>();
        var suppressedFindings = new List<Finding>();
        foreach (var f in allFindings)
        {
            // Suppression matching happens against the rule-reported (absolute) path, since
            // that's what SuppressionIndex was built from; the path is normalized to be relative
            // to the scan root only on the way into the final, reported finding - so the report
            // is portable across machines/CI without weakening suppression matching.
            var isSuppressed = suppression.IsSuppressed(f.FilePath, f.LineNumber, f.RuleId);
            var relative = f with { FilePath = PathUtil.ToRelative(f.FilePath, targetRoot) };
            (isSuppressed ? suppressedFindings : keptFindings).Add(relative);
        }

        var blockers = keptFindings.Count(f => f.Severity == Severity.Blocker);
        var warnings = keptFindings.Count(f => f.Severity == Severity.Warning);
        var info = keptFindings.Count(f => f.Severity == Severity.Info);

        int Order(Finding a, Finding b)
        {
            var bySeverity = a.Severity.CompareTo(b.Severity);
            if (bySeverity != 0) return bySeverity;
            var byFile = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
            return byFile != 0 ? byFile : a.LineNumber.CompareTo(b.LineNumber);
        }
        keptFindings.Sort(Order);
        suppressedFindings.Sort(Order);

        return new ScanSummary(
            targetPath, DateTime.UtcNow, totalFiles, csharpFiles, scannedProjectCount,
            blockers, warnings, info,
            ReadinessScorer.Score(keptFindings),
            keptFindings,
            SuppressedFindings: suppressedFindings,
            NewBlockersCount: blockers,
            NewWarningsCount: warnings,
            NewInfoCount: info,
            BaselineApplied: false,
            PackageCompatibilityChecked: _packageChecker != null,
            PackageCompatibilityTargetMajor: _packageChecker?.TargetCmsMajor);
    }

    // A project is treated as a test project if it declares <IsTestProject>true</IsTestProject>
    // or references the test SDK or a mainstream test framework.
    private static readonly string[] TestPackageMarkers =
        { "Microsoft.NET.Test.Sdk", "xunit", "NUnit", "MSTest.TestFramework", "MSTest.TestAdapter" };

    public static bool IsTestProject(ProjectContext ctx) =>
        ctx.ProjectFileContent.Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase) ||
        ctx.PackageReferences.Any(p => TestPackageMarkers.Any(m => p.Equals(m, StringComparison.OrdinalIgnoreCase)));

    // Swaps each source file's separately-parsed tree for the compilation's own tree of the same
    // path, so rules can ask the compilation for a semantic model of that exact tree.
    private static ProjectContext WithCompilation(ProjectContext ctx, Microsoft.CodeAnalysis.Compilation? compilation)
    {
        if (compilation == null) return ctx;
        var byPath = compilation.SyntaxTrees
            .Where(t => !string.IsNullOrEmpty(t.FilePath))
            .GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var sources = ctx.SourceFiles
            .Select(f => byPath.TryGetValue(f.FilePath, out var tree) ? f with { SyntaxTree = tree } : f)
            .ToList();
        return ctx with { Compilation = compilation, SourceFiles = sources };
    }

    private IEnumerable<Finding> CheckPackages(ProjectContext ctx)
    {
        var checker = _packageChecker!;
        var ids = ctx.PackageReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) yield break;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var tasks = ids.Select(id => checker.CheckAsync(id, ctx.PackageVersions?.GetValueOrDefault(id), cts.Token)).ToList();

        // Task.WhenAll rethrows only the first exception (and nothing at all on cancellation
        // beyond the OperationCanceledException), which is not enough information to know WHICH
        // package(s) failed. The per-task inspection below is what actually drives reporting, so
        // this broad catch only needs to stop the single rethrow from propagating out of the loop.
        try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
        catch { /* individual results are inspected per-task below */ }

        for (var i = 0; i < ids.Count; i++)
        {
            var task = tasks[i];
            PackageCompatResult result;
            if (task.IsCompletedSuccessfully)
            {
                result = task.Result;
            }
            else
            {
                // Previously, a faulted or cancelled per-package check simply vanished from the
                // report with no trace - the package looked clean when its compatibility was
                // actually never established. Every package now gets either a real verdict or an
                // explicit Unknown finding naming it, plus a warning identifying which package and
                // why, so coverage gaps are visible instead of silent.
                var why = task.IsCanceled
                    ? "the check timed out"
                    : $"the check failed ({Unwrap(task.Exception)?.GetType().Name ?? "unknown error"})";
                _warn($"package compatibility check for '{ids[i]}' did not complete ({why}); reporting it as Unknown.");
                result = new PackageCompatResult(ids[i], ctx.PackageVersions?.GetValueOrDefault(ids[i]), PackageCompatStatus.Unknown,
                    Reason: $"The compatibility check for '{ids[i]}' did not complete ({why}).");
            }

            var finding = PackageCompatibilityFindings.ToFinding(result, ctx, checker.TargetCmsMajor);
            if (finding != null) yield return finding;
        }
    }

    private static Exception? Unwrap(AggregateException? ex) => ex?.Flatten().InnerExceptions.FirstOrDefault();

    private static bool IsStrictlyUnder(string candidate, string parent)
    {
        var p = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(string path, string root)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                  .SkipLast(1)
                  .Any(seg => ExcludedSegments.Contains(seg, StringComparer.OrdinalIgnoreCase));
    }

    // Projects a solution actually lists (.sln or .slnx), resolved against the solution's folder.
    // Null when nothing usable could be read, so the caller can fall back to a folder scan.
    private static List<string>? ProjectsFromSolution(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        var dir = Path.GetDirectoryName(full)!;
        if (!TryReadBounded(full, out var text)) return null;

        IEnumerable<string?> relativePaths;
        if (full.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(new StringReader(text), settings);
                relativePaths = XDocument.Load(reader).Descendants("Project").Select(e => e.Attribute("Path")?.Value).ToList();
            }
            catch (XmlException) { return null; }
        }
        else
        {
            relativePaths = System.Text.RegularExpressions.Regex.Matches(
                    text, @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*"",\s*""([^""]+)""",
                    System.Text.RegularExpressions.RegexOptions.Multiline, TimeSpan.FromSeconds(2))
                .Select(m => (string?)m.Groups[1].Value).ToList();
        }

        var projects = relativePaths
            .Where(p => p != null && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p!.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return projects.Count > 0 ? projects : null;
    }

    private static List<string> DiscoverProjects(string path)
    {
        if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return new List<string> { Path.GetFullPath(path) };

        if (File.Exists(path) && (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            var listed = ProjectsFromSolution(path);
            if (listed != null) return listed;
        }

        string? root = null;
        if (File.Exists(path) && (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
            root = Path.GetDirectoryName(Path.GetFullPath(path));
        else if (Directory.Exists(path))
            root = Path.GetFullPath(path);

        if (root == null) return new List<string>();

        return Directory.EnumerateFiles(root, "*.csproj", Recurse)
            .Where(p => !IsExcluded(p, root))
            .Select(Path.GetFullPath)
            .ToList();
    }

    private ProjectContext LoadProject(string projectPath, string projectDir, List<string> nestedDirs)
    {
        var packages = new List<string>();
        var declaredVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projectFileRead = TryReadBounded(projectPath, out var projectContent);

        if (projectFileRead)
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var stringReader = new StringReader(projectContent);
                using var reader = XmlReader.Create(stringReader, settings);
                var doc = XDocument.Load(reader);
                foreach (var pr in doc.Descendants("PackageReference"))
                {
                    var pid = pr.Attribute("Include")?.Value;
                    var pver = pr.Attribute("Version")?.Value ?? pr.Element("Version")?.Value;
                    if (!string.IsNullOrEmpty(pid) && !string.IsNullOrEmpty(pver)) declaredVersions[pid] = pver;
                }
                packages = doc.Descendants("PackageReference")
                    .Select(p => p.Attribute("Include")?.Value ?? "")
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();
            }
            catch (XmlException) { /* malformed project file: skip package list */ }
        }

        // Directory.Build.props applies to every project beneath it: package references declared
        // there count as the project's own, and so do its build settings.
        var inheritedFiles = FindInheritedBuildProps(projectDir);
        foreach (var propsText in inheritedFiles)
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var propsReader = XmlReader.Create(new StringReader(propsText), settings);
                foreach (var pr in XDocument.Load(propsReader).Descendants("PackageReference"))
                {
                    var pid = pr.Attribute("Include")?.Value;
                    if (string.IsNullOrEmpty(pid) || packages.Contains(pid, StringComparer.OrdinalIgnoreCase)) continue;
                    packages.Add(pid);
                    var pver = pr.Attribute("Version")?.Value ?? pr.Element("Version")?.Value;
                    if (!string.IsNullOrEmpty(pver)) declaredVersions[pid] = pver;
                }
            }
            catch (XmlException) { /* malformed props: its text still counts for setting checks */ }
        }

        bool Owned(string p) => !IsExcluded(p, projectDir) &&
                                !nestedDirs.Any(d => IsStrictlyUnder(p, d) || Path.GetDirectoryName(p)!.Equals(d, StringComparison.OrdinalIgnoreCase));

        var sources = Directory.EnumerateFiles(projectDir, "*.cs", Recurse)
            .Where(Owned)
            .Select(p => TryRead(p, projectDir, parse: true))
            .OfType<FileContext>()
            .ToList();

        var configs = Directory.EnumerateFiles(projectDir, "*.*", Recurse)
            .Where(p => Owned(p) && IsConfigFile(p))
            .Select(p => TryRead(p, projectDir, parse: false))
            .OfType<FileContext>()
            .ToList();

        return new ProjectContext(projectPath, projectDir, packages, sources, configs,
            ProjectFileContent: projectFileRead ? projectContent : "",
            PackageVersions: ResolvePackageVersions(projectPath, projectDir, packages, declaredVersions),
            PackageGraph: FindAssetsFile(projectPath, projectDir) is { } assetsFile ? GetParsedAssetsFile(assetsFile)?.Graph : null,
            InheritedBuildProps: string.Join('\n', inheritedFiles),
            AllPackageVersions: FindAssetsFile(projectPath, projectDir) is { } assetsFile2 ? GetParsedAssetsFile(assetsFile2)?.PackageVersions : null);
    }

    // MSBuild imports the NEAREST Directory.Build.props above a project automatically, and only
    // continues upward if that file imports its parent itself (the documented GetPathOfFileAbove
    // idiom). Mirrors that rule rather than merging every ancestor. Contents are cached per path.
    private readonly Dictionary<string, string?> _buildPropsCache = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> FindInheritedBuildProps(string projectDir)
    {
        var found = new List<string>();
        var dir = new DirectoryInfo(projectDir);
        for (var depth = 0; dir != null && depth < 20; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Directory.Build.props");
            if (!File.Exists(candidate)) continue;

            if (!_buildPropsCache.TryGetValue(candidate, out var text))
                _buildPropsCache[candidate] = text = TryReadBounded(candidate, out var content) ? content : null;
            if (text == null) break;

            found.Add(text);
            var importsParent = text.Contains("GetPathOfFileAbove", StringComparison.OrdinalIgnoreCase) &&
                                text.Contains("Directory.Build.props", StringComparison.OrdinalIgnoreCase);
            if (!importsParent) break;
        }
        return found;
    }

    // Prefers the version restore actually resolved (handles floating ranges and Central Package
    // Management alike, since a restored project.assets.json always records the exact version
    // that was actually installed regardless of how it was declared). Falls back to the literal
    // Version attribute in the project file next. Only as a last resort - when neither a restore
    // has happened nor the PackageReference carries its own Version - falls back to
    // Directory.Packages.props: a Central Package Management project legitimately has NEITHER of
    // the first two for most packages (that is the whole point of CPM - no Version attribute on
    // the PackageReference at all), so without this fallback every CPM-managed package looked
    // "unknown version" to the compatibility checker whenever the project hadn't been restored,
    // which in turn made the checker search for a newer "first compatible version" even when the
    // version already centrally pinned was itself already compatible - a false "upgrade needed"
    // report pointing at a version the project was already on.
    private Dictionary<string, string> ResolvePackageVersions(
        string projectPath, string projectDir, IReadOnlyList<string> packages, Dictionary<string, string> declared)
    {
        var assetsPath = FindAssetsFile(projectPath, projectDir);
        var resolved = assetsPath != null ? GetParsedAssetsFile(assetsPath)?.PackageVersions : null;

        // Looked up lazily - only parsed if some package in this project actually needs it - and
        // only probed for a Directory.Packages.props once per project, regardless of how many
        // packages fall through to it.
        IReadOnlyDictionary<string, string>? central = null;
        var centralLookupAttempted = false;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in packages)
        {
            if (resolved != null && resolved.TryGetValue(id, out var resolvedVersion))
            {
                result[id] = resolvedVersion;
                continue;
            }
            if (declared.TryGetValue(id, out var declaredVersion))
            {
                result[id] = declaredVersion;
                continue;
            }

            if (!centralLookupAttempted)
            {
                central = FindCentralPackageVersions(projectDir);
                centralLookupAttempted = true;
            }
            if (central != null && central.TryGetValue(id, out var centralVersion))
                result[id] = centralVersion;
        }
        return result;
    }

    // The assets file is normally <projectDir>/obj/project.assets.json, but solutions that redirect
    // BaseIntermediateOutputPath keep it in an ancestor's obj (e.g. src/backend/obj/<Project>/).
    // Ancestor candidates are accepted only if the file records this project as its restore target.
    private string? FindAssetsFile(string projectPath, string projectDir)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        var dir = new DirectoryInfo(projectDir);
        for (var depth = 0; dir != null && depth < 5; depth++, dir = dir.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir.FullName, "obj", "project.assets.json"),
                         Path.Combine(dir.FullName, "obj", name, "project.assets.json")
                     })
            {
                if (File.Exists(candidate) && AssetsFileTargets(candidate, projectPath)) return candidate;
            }
        }
        return null;
    }

    private bool AssetsFileTargets(string assetsPath, string projectPath)
    {
        var parsed = GetParsedAssetsFile(assetsPath);
        return parsed?.ProjectPath != null &&
               string.Equals(Path.GetFullPath(parsed.ProjectPath), Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ParsedAssetsFile(
        string? ProjectPath,
        IReadOnlyDictionary<string, string> PackageVersions,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Graph);

    // Parses a given project.assets.json exactly once per Scan() call, regardless of how many
    // times it is examined: FindAssetsFile's ancestor walk probes the same candidate paths once
    // per project in a solution, and (before this existed) the file actually owned by a project
    // was parsed a second time to extract package versions after already being parsed once to
    // confirm ownership. A null result (file missing/unreadable/malformed) is cached too, so a
    // rejected candidate is not re-read by every sibling project that happens to probe it.
    private ParsedAssetsFile? GetParsedAssetsFile(string assetsPath)
    {
        if (_assetsFileCache.TryGetValue(assetsPath, out var cached)) return cached;

        ParsedAssetsFile? result = null;
        if (TryReadBounded(assetsPath, out var json) || TryReadLarge(assetsPath, out json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);

                string? ownerProjectPath = null;
                if (doc.RootElement.TryGetProperty("project", out var project) &&
                    project.TryGetProperty("restore", out var restore) &&
                    restore.TryGetProperty("projectPath", out var pathEl))
                {
                    ownerProjectPath = pathEl.GetString();
                }

                var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (doc.RootElement.TryGetProperty("libraries", out var libraries))
                {
                    foreach (var lib in libraries.EnumerateObject())
                    {
                        // "Id/Version"; only packages (not projects).
                        if (lib.Value.TryGetProperty("type", out var type) && type.GetString() != "package") continue;
                        var slash = lib.Name.IndexOf('/');
                        if (slash > 0) versions.TryAdd(lib.Name[..slash], lib.Name[(slash + 1)..]);
                    }
                }

                // targets -> <first framework> -> "Id/Version" -> { dependencies: { Id: range } }
                var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                if (doc.RootElement.TryGetProperty("targets", out var targets))
                {
                    foreach (var framework in targets.EnumerateObject())
                    {
                        foreach (var entry in framework.Value.EnumerateObject())
                        {
                            var slash = entry.Name.IndexOf('/');
                            if (slash <= 0) continue;
                            if (entry.Value.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
                                graph.TryAdd(entry.Name[..slash], deps.EnumerateObject().Select(d => d.Name).ToList());
                        }
                        break; // the first target framework is representative for dependency shape
                    }
                }

                result = new ParsedAssetsFile(ownerProjectPath, versions, graph);
            }
            catch (JsonException) { /* unreadable/malformed: cache the miss so it isn't retried */ }
        }

        _assetsFileCache[assetsPath] = result;
        return result;
    }

    // Central Package Management stores package versions in Directory.Packages.props, found by
    // walking up from the project directory - exactly like Directory.Build.props - rather than
    // living next to any one project. Bounded more generously than the assets-file ancestor walk
    // (20 vs. 5 levels): Directory.Packages.props is conventionally at the repository/solution
    // root, which can legitimately sit several directories above a deeply nested project
    // (e.g. src/Areas/Commerce/Catalog/Catalog.csproj).
    private IReadOnlyDictionary<string, string>? FindCentralPackageVersions(string projectDir)
    {
        var dir = new DirectoryInfo(projectDir);
        for (var depth = 0; dir != null && depth < 20; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (File.Exists(candidate)) return GetParsedCentralPackageVersions(candidate);
        }
        return null;
    }

    // Parses a given Directory.Packages.props exactly once per Scan() call. A null result (file
    // unreadable/malformed) is cached too, for the same reason GetParsedAssetsFile caches a miss.
    private IReadOnlyDictionary<string, string>? GetParsedCentralPackageVersions(string path)
    {
        if (_centralPackageVersionsCache.TryGetValue(path, out var cached)) return cached;

        Dictionary<string, string>? result = null;
        if (TryReadBounded(path, out var content))
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var stringReader = new StringReader(content);
                using var reader = XmlReader.Create(stringReader, settings);
                var doc = XDocument.Load(reader);

                var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pv in doc.Descendants("PackageVersion"))
                {
                    var id = pv.Attribute("Include")?.Value;
                    var version = pv.Attribute("Version")?.Value;
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version)) versions[id] = version;
                }
                result = versions;
            }
            catch (XmlException) { /* malformed: cache the miss so it isn't retried */ }
        }

        _centralPackageVersionsCache[path] = result;
        return result;
    }

    // project.assets.json is routinely larger than MaxFileBytes on big solutions and is
    // machine-generated JSON we only parse, so it gets its own, larger bound.
    private static bool TryReadLarge(string path, out string content)
    {
        try
        {
            if (new FileInfo(path).Length > 100 * 1024 * 1024) { content = ""; return false; }
            content = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            content = "";
            return false;
        }
    }

    private static bool IsConfigFile(string p)
    {
        var name = Path.GetFileName(p);
        return name.Equals("web.config", StringComparison.OrdinalIgnoreCase) ||
               (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static FileContext? TryRead(string path, string projectDir, bool parse)
    {
        if (!TryReadBounded(path, out var content)) return null;
        var tree = parse ? CSharpSyntaxTree.ParseText(content, path: path) : null;
        return new FileContext(path, Path.GetRelativePath(projectDir, path), content, tree);
    }

    // Shared bounded file read: every file the scanner looks at (source, config, and now also the
    // project file itself) goes through this one guard, so none of them can bypass the size limit
    // or silently blow up on a transient I/O error.
    private static bool TryReadBounded(string path, out string content)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes)
            {
                content = "";
                return false;
            }
            content = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            content = "";
            return false;
        }
    }
}

public static class ReadinessScorer
{
    private const int BlockerRulePenalty = 20;
    private const int WarningRulePenalty = 8;
    private const int InfoRulePenalty = 2;

    /// <summary>
    /// Scored by the number of DISTINCT rules triggered per severity, not the number of
    /// occurrences. A codebase with 200 call sites all hitting the same rule loses the same
    /// points as one with 2 - so the score reflects the breadth of problem types rather than
    /// flooring to 0 the moment one widespread pattern appears a few dozen times, and it still
    /// discriminates between "one narrow problem" and "many different kinds of problems."
    /// </summary>
    public static int Score(IReadOnlyList<Finding> findings)
    {
        int DistinctRules(Severity s) =>
            findings.Where(f => f.Severity == s).Select(f => f.RuleId).Distinct().Count();

        var penalty = DistinctRules(Severity.Blocker) * BlockerRulePenalty
                    + DistinctRules(Severity.Warning) * WarningRulePenalty
                    + DistinctRules(Severity.Info) * InfoRulePenalty;
        return Math.Max(0, 100 - penalty);
    }
}
