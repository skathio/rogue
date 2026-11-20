# SkathIO.Rogue

AOT-safe, source-generated CQRS/mediator for .NET, published to NuGet as nine packages. Handler
discovery and dispatch wiring happen at compile time in a Roslyn incremental generator: no runtime
reflection, no assembly scanning. A static project site lives in `site/` (GitHub Pages).

## Commands (verified on .NET SDK 10.0.1xx; there is no `global.json`)

```bash
dotnet build SkathIO.Rogue.sln -c Release          # ~1 min; zero warnings or it fails
dotnet test SkathIO.Rogue.sln -c Release --no-build # ~300 tests across 10 test projects, ~1 min
dotnet test tests/SkathIO.Rogue.Generator.Tests -c Release   # one project
```

- There is no separate lint step: `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild` make
  `.editorconfig` style and the analyzers part of the build.
- AOT check (as CI runs it; needs `clang` and `zlib1g-dev`):
  `dotnet publish samples/SkathIO.Rogue.Aot.Sample/SkathIO.Rogue.Aot.Sample.csproj -c Release -r linux-x64`
  then run `samples/SkathIO.Rogue.Aot.Sample/bin/Release/net10.0/linux-x64/publish/SkathIO.Rogue.Aot.Sample`.
- Benchmark smoke run, as CI runs it:
  `dotnet run --project bench/SkathIO.Rogue.Benchmarks/SkathIO.Rogue.Benchmarks.csproj -c Release -- --filter '*NoBehavior*' --job short --exporters json`

## Layout

- `src/SkathIO.Rogue.Abstractions`: contracts only (`IQuery`, `ICommand`, handlers, `Unit`).
  Targets `net8.0;net10.0`, like every runtime package (netstandard2.0 was dropped in 2.0.0).
- `src/SkathIO.Rogue`: runtime (`RogueDispatcher`, `PipelineExecutor`, publishers, `AddRogue`).
  The generators and the migration analyzer stay on `netstandard2.0`, because the compiler loads
  analyzers only from that target.
- `src/SkathIO.Rogue.SourceGenerator`: the generator (`RogueGenerator.cs`), with
  `DispatcherEmitter.cs` / `RegistrationEmitter.cs` writing C# through `CodeWriter`. The long
  `Emit*` methods with bool shape flags are deliberate: they mirror the code they emit.
  Diagnostics live in `DiagnosticDescriptors.cs`; a new one needs an `AnalyzerReleases.Unshipped.md` row.
- `src/SkathIO.Rogue.MediatR` + `src/SkathIO.Rogue.Migration.Analyzer`: MediatR compat shim and
  migration analyzer. `MediatR/EscapeHatch/ReflectionMediator.cs` is the **only** allowed use of
  reflection; do not add another.
- `src/SkathIO.Rogue.Logging`, `src/SkathIO.Rogue.Validation.FluentValidation` (+ its own generator
  for validator auto-discovery).
- `tests/`: xunit. `Generator.Tests` drives the generator in-memory; `Smoke.*` is a deliberate
  multi-project solution (Infrastructure/Application/Api/Tests) that catches cross-assembly wiring bugs.
- `samples/`, `bench/` (BenchmarkDotNet; committed baselines in `bench/results/<date>-<sha>/`,
  allocation thresholds in `.bench/thresholds.json`), `docs/`, `site/`.

## Conventions

- Nullable on, `ImplicitUsings` off (write `using`s), private fields `_camelCase`, braces always.
- Public API is tracked by PublicApiAnalyzers. A new public member needs a `///` doc comment and an
  entry in `PublicAPI.Unshipped.txt` (one pair per project, at the project root).
- Packages are pinned centrally in `Directory.Packages.props` (no versions in csproj).
- Any new package in the restore graph of a packable project (build-only tools included)
  must be added to `.github/allowed-packages.txt` with a license annotation, after a human checks the
  license is permissive (`docs/governance.md`, "Dependency policy"). The `license-check` job fails otherwise.
  A new packable project goes into that job's `packable_projects` list and the allow-list header.
- Never reference `MediatR` or `Mediator.*` packages from `src/`; never use `BinaryFormatter`.
- `Microsoft.CodeAnalysis.CSharp` stays at 5.0.0: it is compiled into the shipped generator, and a
  higher pin breaks consumers on older SDKs. Dependabot ignores it on purpose.
- Older comments cite ids such as `PD-17` or `NFR-PERF-1`. They point at planning notes in `.somi/`,
  which is gitignored and not in the public repo. In new comments, give the reason in words.
- Commits: Conventional Commits (`feat:`, `fix:`, `docs(changelog):`, `ci:`, `chore(deps):`), branches
  `feat/…`, `fix/…`, `chore/…`, `docs/…`. PRs target `main`. User-visible changes go under
  `## [Unreleased]` in `CHANGELOG.md` (Keep a Changelog).

## CI and release

- `.github/workflows/ci.yml` on every PR: `ci` (hashira's reusable `nuget-package-ci.yml@v2`:
  build, `dotnet test` on the solution, scans, coverage), `AOT publish`, `Benchmark smoke` (fails if
  the `ISender.Send` path allocates over the ceiling in `.bench/thresholds.json`), `License check`,
  `Public API`.
- hashira is pinned twice (`uses: …@v2` and `library_ref: 'v2'`); keep the two pins identical.
- Releases are manual: the maintainer dispatches `publish.yml` with a `bump`, and the release needs
  approval in the `production` environment. It packs, pushes to nuget.org over OIDC, tags, then
  deploys `site/`. Prep steps are in the `release-prep` skill. Agents never dispatch a release.
- `site.yml` deploys the site; `homepage.yml` pings skathio.github.io when `.github/homepage.json`
  or `site/mark.svg` changes on main.

## Visual testing (required for anything that renders)

`site/` is the frontend: one static page (`index.html`, `theme.js`, `mark.svg`) with no build
step. It supports light and dark (`prefers-color-scheme`, plus a toggle that sets `data-theme`).
Any change that touches `site/`, or changes what the page shows, must be visually tested before
it is called done:

1. Run `.claude/skills/visual-test/screenshot.sh <out-dir>`. It serves `site/` on a free port and
   takes phone (390px) and desktop (1440px) screenshots in light and dark mode.
2. Open and inspect every screenshot. A width warning means the page scrolls sideways.
3. In the PR, attach the screenshots or describe what each one shows.

Full steps are in the `visual-test` skill.

## Pitfalls

- A fixed port such as 8000 or 8765 may already be served by another repo's site, and you would
  screenshot the wrong page. The script picks a free port and checks the page title is `Rogue`.
- Known issue: the comparison table makes the phone layout 647px wide at a 390px viewport.
- `.claude/` is gitignored except `.claude/skills/`; `.somi/` is gitignored entirely.
- Generator changes can break consumers in other projects even when unit tests pass; run the
  `Smoke.*` tests (they are part of the solution test run).
