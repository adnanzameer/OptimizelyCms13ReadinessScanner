# cms13-scan

[![NuGet](https://img.shields.io/nuget/v/OptimizelyCms13ReadinessScanner?logo=nuget)](https://www.nuget.org/packages/OptimizelyCms13ReadinessScanner)
[![Downloads](https://img.shields.io/nuget/dt/OptimizelyCms13ReadinessScanner)](https://www.nuget.org/packages/OptimizelyCms13ReadinessScanner)
[![CI](https://github.com/adnanzameer/OptimizelyCms13ReadinessScanner/actions/workflows/ci.yml/badge.svg)](https://github.com/adnanzameer/OptimizelyCms13ReadinessScanner/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Pre-flight static analysis scanner for Optimizely CMS 12 codebases ahead of a CMS 13 upgrade.

```bash
dotnet tool install --global OptimizelyCms13ReadinessScanner
cms13-scan ./MySite.sln
```

Point it at a solution, project, or directory and it reports the breaking changes, deprecated
APIs, and configuration drift that will block or complicate a CMS 13 upgrade — before you start
the upgrade, not halfway through it.

```
===============================================================================
 Optimizely CMS 13 Upgrade Readiness Scanner (cms13-scan)
===============================================================================
 Target Path:    C:\src\MySite
 Projects:       3
 C# Files:       842
-------------------------------------------------------------------------------
 Overall Score:  62%
 Summary:        2 Blockers | 6 Warnings | 3 Info
===============================================================================

[BLOCKER] OPT13-001 - Unsupported Search & Navigation (Find) Packages
  File: C:\src\MySite\Web\Web.csproj:12
  Note: Project references unsupported package 'EPiServer.Find.Cms'. Search & Navigation is not supported in CMS 13.
  Fix:  Remove the package and install 'Optimizely.Graph.Cms.Query' and 'Optimizely.Graph.AspNetCore'.
```

## Install

`cms13-scan` is a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/global-tools) published on
[nuget.org](https://www.nuget.org/packages/OptimizelyCms13ReadinessScanner). You need the
[.NET 8 SDK or newer](https://dotnet.microsoft.com/download).

```bash
dotnet tool install --global OptimizelyCms13ReadinessScanner
```

Check it works:

```bash
cms13-scan --help
```

Update to the latest version, or remove it:

```bash
dotnet tool update --global OptimizelyCms13ReadinessScanner
```

```bash
dotnet tool uninstall --global OptimizelyCms13ReadinessScanner
```

To install it for one repository only (so everyone on the team gets the same version), use a
[local tool manifest](https://learn.microsoft.com/dotnet/core/tools/local-tools-how-to-use) and run it with `dotnet cms13-scan`:

```bash
dotnet new tool-manifest
```

```bash
dotnet tool install OptimizelyCms13ReadinessScanner
```

The tool targets .NET 8 but is configured with `RollForward=Major`, so it also starts on a machine
that only has .NET 10 (tested: the packaged tool, including `--semantic`, on the .NET 10 runtime).
It analyses your solution as a separate process, so the solution being scanned can be on any framework.

Want to build it yourself or contribute? See [Building and testing from source](#building-and-testing-from-source).

## Usage

```
cms13-scan [path] [options]
```

`path` is a `.sln`, a `.csproj`, or a directory to scan recursively. Defaults to the current
directory.

| Option | Description |
|---|---|
| `--output-markdown <file>` | Write a Markdown readiness report |
| `--output-json <file>` | Write structured JSON results (also the `--baseline` input format) |
| `--output-sarif <file>` | Write a SARIF 2.1.0 report for GitHub code scanning / Azure DevOps PR annotations |
| `--semantic` | Compile projects via MSBuild (must already be restored; only for trusted code, see [Security](#security-scanning-code-you-dont-trust)) to confirm findings against the real symbol/type model instead of syntax pattern-matching alone |
| `--baseline <file>` | Compare against a prior `--output-json` report; only findings new since the baseline fail the build |
| `--rules <file>` | Merge in additional or overriding rule definitions (see [Custom rules](#custom-rules)) |
| `--list-rules` | Print the effective rule set (built-in + any `--rules` merge) as JSON and exit |
| `--exclude-tests` | Skip test projects (referencing a test SDK/framework, or setting `IsTestProject`) so their findings do not count toward the score |
| `--check-packages` | Check every direct NuGet package against the feeds in your `nuget.config` (https only) for CMS compatibility, reported as `OPT13-010`. Sends package ids to those feeds, like `dotnet restore` does; off by default |
| `--cms-target-major` | CMS major version `--check-packages` tests against (default `13`) |
| `--check-api` | Download the CMS 13 assemblies from the feeds in your `nuget.config` and report APIs your code uses that are gone or obsolete there (`OPT13-013`). Needs `--semantic` and a restored solution; downloads packages (see [API check](#api-check---check-api)) |
| `--no-fail` | Always exit 0, even with blockers (default: exit 1 on any new blocker, for CI gating) |

### Typical runs

A quick first look. It only reads files and needs nothing else:

```bash
cms13-scan ./MySite.sln --exclude-tests
```

The accurate run. Restore first (`dotnet restore`), then add the semantic, package and API checks and
save a report you can share:

```bash
cms13-scan ./MySite.sln --semantic --check-packages --check-api --exclude-tests --no-fail --output-markdown cms13-report.md
```

In CI, fail the build only on problems introduced since a saved baseline:

```bash
cms13-scan ./MySite.sln --baseline baseline.json --output-sarif cms13.sarif
```

Exit code is `1` when new blockers are found (`0` with `--no-fail`), `2` if the target path
doesn't exist. "New" means "not already present in `--baseline`" when one is supplied, otherwise
every blocker found.

## Why `--semantic` matters

Without it, several rules can only pattern-match on syntax: a method named `GetResult()` might be
an `EPiServer.Find` query, or it might be `Task<T>.GetAwaiter().GetResult()`, or a connection-pool
helper with the same name — the syntax tree alone can't tell. Those findings are reported as
**unverified Warnings** rather than Blockers, and say so explicitly:

```
Access to 'ContentArea.FilteredItems' detected. In CMS 13 this property is marked [Obsolete(error: true)], so any use fails to compile.
(unverified: syntax-only match; re-run with --semantic to confirm)
```

`--semantic` compiles each project through MSBuild and checks the real resolved symbol, which
either confirms the finding (and escalates it to a Blocker where applicable) or clears it
entirely. It needs the target already `dotnet restore`d, and degrades gracefully — with a
warning, not a failure — if MSBuild can't be located.

## Readiness score

The score is **not** based on how many occurrences were found — it's based on how many *distinct
rules* were triggered, per severity:

```
Score = 100 - (distinct Blocker rules × 20) - (distinct Warning rules × 8) - (distinct Info rules × 2)
```

A rule that fires 200 times across a large codebase costs the same as one that fires twice. This
is deliberate: an occurrence-weighted score floors to 0% almost immediately on any real codebase,
at which point it stops telling you anything. A rule-breadth score keeps discriminating between "one
narrow, repetitive problem" and "many different kinds of problems" — which is what actually
drives upgrade effort.

## Rules

| Rule | Severity | What it catches |
|---|---|---|
| `OPT13-001` | Blocker | References to unsupported Search & Navigation (Find) packages |
| `OPT13-002` | Blocker | Synchronous Find query execution (`.GetResult()`, `.GetContentResult()`, `SearchClient.Instance`) |
| `OPT13-003` | Blocker | Use of `ContentArea.FilteredItems`, which CMS 13 marks `[Obsolete(error: true)]` (verified against the CMS 13.0.0 assembly: it still exists but no longer compiles) |
| `OPT13-004` | Warning | Direct calls to deprecated `FilterAccess.QueryDistinctAccess*` methods |
| `OPT13-005` | Warning | Legacy `EPiServer:Find` configuration sections and hardcoded Graph credentials, in both `appsettings*.json` and `web.config` |
| `OPT13-006` | Info | `<TreatWarningsAsErrors>` not enabled (makes obsolete-API warnings easy to miss before an upgrade) |
| `OPT13-007` | Warning/Blocker | Any `[Obsolete]` Optimizely/EPiServer API usage — requires `--semantic` |
| `OPT13-008` | Warning | Use of `ISiteDefinitionRepository` / `SiteDefinitionRepository` / `SiteDefinition`, to review against the CMS 13 Application model (syntax-only, name match) |
| `OPT13-009` | Info | Optimizely project whose `<TargetFramework(s)>` does not include .NET 10 or later |
| `OPT13-010` | Warning/Info | Third-party package whose declared CMS dependency does not admit the target CMS version (with the first compatible release when one exists), or whose status could not be determined — requires `--check-packages` |
| `OPT13-011` | Blocker | File imports an `EPiServer.Find` namespace (reported once per file) — the broadest signal that code depends on the Find API |
| `OPT13-012` | Warning | A direct package that depends on `EPiServer.Find` (shortest chain is shown) — needs a restored project (`obj/project.assets.json`) |
| `OPT13-013` | Blocker/Warning | An Optimizely/EPiServer type or member the code uses that is gone from, or `[Obsolete]` in, the real CMS 13 assemblies (error-level obsolete = Blocker, plain obsolete = Warning; Optimizely's own message is used as the fix) — requires `--semantic --check-api` |

`OPT13-002`, `OPT13-003`, and `OPT13-007` depend on the semantic model to confirm a finding or
escalate its severity; see [Why `--semantic` matters](#why---semantic-matters). The others are
pure pattern matches and always report at full confidence.

### Custom rules

`OPT13-001`, `OPT13-004`, `OPT13-006`, `OPT13-008`, and `OPT13-009` are **data-driven** — defined in
[`src/CmsUpgradeScanner/rules.json`](src/CmsUpgradeScanner/rules.json), not hardcoded in C#. Run
`cms13-scan --list-rules` to see the exact JSON shape for each kind.

Pass `--rules <file>` with the same shape to extend or override them, no recompile needed:

```json
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
```

A definition with an `id` matching a built-in rule **replaces** it; a new `id` is **added**
alongside the built-ins. Supported `kind`s:

| Kind | Fields used | Checks |
|---|---|---|
| `ProjectPackageReference` | `packages` | Any of the listed package names referenced in the `.csproj` |
| `MemberAccessPattern` | `memberNamePrefix`, `expressionContains` (optional) | A member access (`x.Name`) whose member name starts with `memberNamePrefix`, optionally also requiring the full expression text to contain `expressionContains` |
| `ProjectFileMustContain` | `requiredText`, `onlyIfOptimizelyProject` (optional) | Flags the project file if it does **not** contain `requiredText` |
| `IdentifierUsage` | `identifiers` | Any identifier in C# source whose text equals one of the listed names (syntax-only; an unrelated same-named type also matches) |
| `ProjectTargetFramework` | `minimumDotNetMajor` | Optimizely projects whose `<TargetFramework(s)>` has no `net{N}.0` moniker with N >= `minimumDotNetMajor` (skipped if the framework is set outside the project file) |

`{package}`, `{match}` and `{framework}` placeholders in `message` are substituted with the matched value.

Rules needing real semantic analysis or structural (JSON/XML) parsing — `OPT13-002`, `003`,
`005`, `007` — stay as C# and can't currently be authored as data.

## Package compatibility (`--check-packages`)

Instead of a hand-maintained compatibility list, the scanner asks your NuGet feeds. For each direct
package it uses the version restore resolved (`obj/project.assets.json`, falling back to the
`Version` in the `.csproj`) and reads the declared dependencies of that version:

| Result | Meaning | Finding |
|---|---|---|
| Compatible | Every `EPiServer.CMS.*` / `Optimizely.CMS.*` dependency range admits the target major | none |
| Compatible, newer .NET 10 build exists | Declared compatible (typically an open-ended range), but a newer release targets only .NET 10+, likely the build made for the new platform | Info, names the release |
| Upgrade available | This version is capped below the target, but a newer release is not | Warning, names the first compatible version |
| No compatible release | No published release admits the target | Warning |
| Unknown | Not found on the configured feeds, or a feed was unreachable | Info |
| Not CMS-dependent | No CMS dependency (e.g. Serilog); CMS compatibility does not apply | none |

Things to know:
- **"Compatible" means the package's declared dependency range allows the target — not that it was
  tested.** A package can declare an open-ended range and still break.
- First-party packages that depend on another first-party package (e.g.
  `EPiServer.ImageLibrary.ImageSharp` -> `EPiServer.ImageLibrary`) are followed up to three levels,
  and a first-party version that targets only .NET 10+ is treated as belonging to the CMS 13 line.
- `EPiServer.Framework` is deliberately not used as an anchor: its version numbers do not track the CMS major.
- Only `https` feeds are queried. Feeds that require credentials the NuGet configuration cannot
  supply are skipped, and their packages are reported as Unknown.
- Platform packages (`EPiServer.CMS*`, `Optimizely.CMS*`, `Microsoft.*`, `System.*`) are skipped:
  they are the upgrade itself.

## API check (`--check-api`)

The other rules look for a short list of known breaking changes. `--check-api` asks the real CMS 13
assemblies instead, so it finds changes nobody has written a rule for.

1. For every first-party package your solution restored (`EPiServer.*`, `Optimizely.*`), it picks the
   CMS 13 release: the lowest stable release of the target major for CMS platform packages, or the
   first release the package compatibility check says supports the target for the rest.
2. It follows those packages' own first-party dependencies, because CMS 13 split the old core into
   several packages (`EPiServer`, `EPiServer.Cache`, `EPiServer.Blobs`, ...) that a CMS 12 restore
   never lists. Downloads run in parallel.
3. It reads each assembly's metadata (never loading or running it) and records every public and
   protected type and member, plus `[Obsolete]` attributes.
4. For each Optimizely symbol your code resolves to (via `--semantic`), it reports whether the symbol is
   **gone**, **`[Obsolete(error: true)]`** (does not compile - how CMS 13 retires most APIs), or plain
   **`[Obsolete]`**, using Optimizely's own obsolete message as the suggested fix.

Limits to keep in mind:
- **Name-based.** A member counts as present if a member of that name exists on the type or anything it
  inherits. A change of parameters that keeps the name is not detected.
- **Needs a restored solution** (`obj/project.assets.json`), and downloads packages (typically 50-120
  for a CMS site, roughly 100-150 MB; a Commerce site measured 115 packages / 135 MB). The first run depends on your connection (minutes on a slow feed); NuGet's HTTP cache makes repeat runs take seconds. The run prints a one-line summary of what was indexed.
- Packages with no CMS 13 build are listed at the end of the run; they cannot be compared.
- Downloaded packages are read in memory and never extracted or executed, but the same trust rule as
  `--check-packages` applies to the feeds in the scanned folder's `nuget.config`.
- When `--check-api` reports a line that `OPT13-003` also reports, the `OPT13-013` finding replaces it.

## What the rules were verified against

Rule claims were checked against the real packages on the Optimizely feed (CMS 13.0.0 / 13.3.0),
not just against documentation:

| Claim | Result |
|---|---|
| `Optimizely.Graph.Cms.Query` / `Optimizely.Graph.AspNetCore` exist (OPT13-001 fix) | Confirmed (13.x releases) |
| `IGraphContentClient`, `GetAsContentAsync` exist (OPT13-002 fix) | Confirmed, in `Optimizely.Graph.Cms.Query` |
| `ContentArea.FilteredItems` is "removed" (OPT13-003) | **Corrected**: it still exists but is `[Obsolete(error: true)]`; wording and fix now use Optimizely's own message |
| `FilterAccess.QueryDistinctAccessEdit` is deprecated (OPT13-004) | Confirmed: `[Obsolete]` (warning) in 13, "Use IContentAccessEvaluator.HasAccess instead" |
| Site definitions are affected by the Application model (OPT13-008) | Confirmed: `SiteDefinition`, `ISiteDefinitionRepository`, `ISiteDefinitionResolver`, `HostDefinition` are `[Obsolete]` in 13 |

CMS 13 mostly retires APIs by marking them `[Obsolete(error: true)]` rather than deleting them,
which is why `--check-api` reports obsolete-as-error usage and not only missing symbols.

## Directory.Build.props

For each project the scanner reads the `Directory.Build.props` MSBuild would import: the nearest one
above the project, plus its parents when it imports them with the usual `GetPathOfFileAbove` idiom
(it does not merge every ancestor, because MSBuild does not). Build settings (`TreatWarningsAsErrors`,
`TargetFramework(s)`) and `PackageReference`s declared there count as the project's own, so they no
longer cause false "missing" findings or make an Optimizely project look like a plain one.
`Directory.Build.targets` and conditional property groups are not evaluated.

## Security: scanning code you don't trust

The default scan only reads files: it parses C#, XML and JSON and never runs any of it. Two options go further:

- **`--semantic`** loads each project through MSBuild, which evaluates the project's `.csproj`,
  `Directory.Build.props/targets` and imports, and can run build targets from the scanned repository.
  **Only use `--semantic` on code you trust** (your own repo, or a CI checkout of it), not on an
  unreviewed third-party repository or an untrusted pull request.
- **`--check-packages`** and **`--check-api`** use the NuGet feeds defined in the scanned folder's `nuget.config` chain.
  Package ids (and any credentials configured for that feed) go to those feeds, exactly as with
  `dotnet restore`. A repository can point `nuget.config` at a feed it controls, so apply the same trust rule.

Reports (`--output-*`) contain file paths and finding text, never the values of hardcoded secrets.

## Suppressing a finding

Inline comments, independent of the host file's comment syntax — works in C# `//` comments, XML
`<!-- -->` in a `.csproj`, anywhere text can appear on that line:

```csharp
var items = area.FilteredItems; // cms13-scan:disable OPT13-003
```

```csharp
// cms13-scan:disable-next-line OPT13-002
var r = SearchClient.Instance.Search<object>().GetContentResult();
```

- `cms13-scan:disable` with no rule ID suppresses every rule on that line.
- `cms13-scan:disable OPT13-002,OPT13-004` suppresses only the listed rules.
- `cms13-scan:disable-next-line [...]` applies the same rules to the line that follows instead.

Suppressed findings are excluded from the score, the counts, and the exit code — but never
silently dropped. They're still listed (console: a `SUPPRESSED` section; Markdown: a "Suppressed
Findings" appendix; SARIF: `result.suppressions` with `kind: inSource`, which GitHub renders as
dismissed rather than missing), so a suppression is always visible to a reviewer.

> JSON config files (`appsettings*.json`) have no comment syntax, so an `OPT13-005` finding there
> can't be suppressed this way.

## Baselining an existing codebase

Adopting the scanner against a codebase that already has known issues shouldn't mean fixing
everything before CI goes green:

```powershell
# capture current state once
cms13-scan . --output-json baseline.json --no-fail

# every subsequent run only fails on NEW findings
cms13-scan . --baseline baseline.json
```

Findings already in the baseline are kept in the output (tagged `(baseline)` / "Known" /
`baselineState: "unchanged"` in SARIF) but excluded from the exit code. Matching is by rule +
path-relative-to-scan-root + line number, so it survives being run from a different checkout path
(a laptop vs. a CI runner) and survives a finding's message text changing between `--semantic` and
non-semantic runs.

## CI integration

See [`.github/workflows/ci.yml`](.github/workflows/ci.yml) for a working example: build, test,
then a self-scan of the `sample/` fixture that uploads a SARIF report to GitHub code scanning.

For scanning your own CMS 12 solution in CI:

```yaml
- name: CMS 13 upgrade readiness scan
  run: |
    dotnet tool run cms13-scan . \
      --output-sarif cms13-scan.sarif \
      --baseline cms13-scan-baseline.json

- name: Upload SARIF
  if: always()
  uses: github/codeql-action/upload-sarif@v3
  with:
    sarif_file: cms13-scan.sarif
```

## Releasing

Releases are published to nuget.org by `.github/workflows/publish.yml` when a version tag is pushed.
The package version comes from the tag, and the workflow builds, runs every test, packs, installs
the packed tool and starts it before it pushes anything.

One-time setup:
1. On nuget.org create an API key (scope: Push, glob `OptimizelyCms13ReadinessScanner`).
2. In the GitHub repo create an environment named `nuget` (Settings > Environments), add yourself as
   a required reviewer for an approval gate, and add the key as the secret `NUGET_API_KEY` there.

Each release:

```bash
git tag v1.0.0
git push origin v1.0.0
```

Tags must look like `v1.2.3` or `v1.2.3-beta.1`; anything else is rejected. A published version
cannot be replaced, only unlisted, so check the README and CHANGELOG before tagging. Re-running a
failed publish is safe (`--skip-duplicate`).

## Building and testing from source

```bash
git clone https://github.com/adnanzameer/OptimizelyCms13ReadinessScanner.git
cd OptimizelyCms13ReadinessScanner
dotnet build CmsUpgradeScanner.sln
dotnet test CmsUpgradeScanner.sln
```

Run the code you just built, without installing anything:

```bash
dotnet run --project src/CmsUpgradeScanner -c Release -- ./sample
```

Or pack it and install your build as the global tool (replacing the nuget.org one):

```bash
dotnet pack src/CmsUpgradeScanner -c Release -o ./nupkg
```

```bash
dotnet tool install --global --add-source ./nupkg OptimizelyCms13ReadinessScanner
```

`src/CmsUpgradeScanner` is the tool; `tests/CmsUpgradeScanner.Tests` is an xUnit suite covering
every rule (including dedicated regression tests for the false-positive fixes below), the
suppression/baseline/SARIF/rule-manifest machinery, and the readiness scorer. `sample/` is a small
fixture solution the scanner is self-tested against; its expected output is the committed snapshot
`tests/CmsUpgradeScanner.Tests/Snapshots/sample.expected.json`. Any `report.md` / `report.json` /
`report.sarif` at the repo root are local scan output and are git-ignored.

### Design notes for contributors

- **Findings only escalate to Blocker when they can be proven.** A syntax-only match (no
  `--semantic`, or a receiver type that can't be resolved, e.g. `dynamic`) is reported as an
  "unverified" Warning instead — a static analyzer that produces false-positive Blockers gets
  `--no-fail`'d within a week and stops being useful.
- **Finding.FilePath is scan-root-relative**, not absolute, once a scan completes — portable
  across machines and diffable in source control. `ConsoleReporter` reconstructs an absolute path
  purely for local display.
- **Rules read `ProjectContext.ProjectFileContent`**, never the filesystem directly — makes them
  testable without disk I/O and keeps the 5 MB file-size guard uniform across every file the
  scanner touches.
