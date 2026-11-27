# SkathIO.Rogue

AOT-safe, source-generated CQRS/mediator for .NET, published to NuGet as twelve packages. Handler
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
- Consumer suite, as CI runs it: `tests/consumer/run.sh` packs the twelve packages into
  `artifacts/consumer/feed` and tests `tests/consumer/SkathIO.Rogue.Consumer.sln`, a separate
  solution that restores them by package reference (~4 min). Its Postgres and SQL Server job tests
  need Docker: without it they skip locally and fail in CI (`CI`/`GITHUB_ACTIONS` set). It then
  runs `tests/consumer/aot.sh`, which publishes both AOT samples natively from the feed and runs
  them; without `clang` that step skips locally and fails in CI.
- AOT check (as CI runs it; needs `clang` and `zlib1g-dev`):
  `dotnet publish samples/SkathIO.Rogue.Aot.Sample/SkathIO.Rogue.Aot.Sample.csproj -c Release -r linux-x64`
  then run `samples/SkathIO.Rogue.Aot.Sample/bin/Release/net10.0/linux-x64/publish/SkathIO.Rogue.Aot.Sample`.
  CI does the same for the jobs and messaging sample,
  `samples/SkathIO.Rogue.Jobs.Messaging.Aot.Sample/SkathIO.Rogue.Jobs.Messaging.Aot.Sample.csproj`,
  and runs its binary.
- Benchmark smoke run, as CI runs it:
  `dotnet run --project bench/SkathIO.Rogue.Benchmarks/SkathIO.Rogue.Benchmarks.csproj -c Release -- --filter '*NoBehavior*' --job short --exporters json`
- Docs site: CI builds it with hashira's `docs-build` action (`@v3`), whose pre-build command
  (in `site.yml`, `ci.yml` and `publish.yml`, kept equal) generates the API reference with docfx
  and `tools/docs-api/clean-api.mjs`; a dead link fails it. Locally, `tools/docs-api/build-pages.sh`
  (needs Node 24, the .NET SDK and network, or `HASHIRA_DIR` set to a hashira clone) runs the same
  steps and assembles `artifacts/pages`. `node --test tools/docs-api/` runs the docs tooling tests
  (it runs docfx, ~3 min).

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
- Jobs family: `src/SkathIO.Rogue.Jobs` (scheduler, worker, `IJobStore`, in-memory store; the
  `Jobs.Analyzers`/`Jobs.CodeFixes` projects ship inside it), `src/SkathIO.Rogue.Jobs.Postgres`,
  `src/SkathIO.Rogue.Jobs.SqlServer` (their shared code is `src/SkathIO.Rogue.Jobs/StoreShared/`,
  compiled into each).
- Messaging family: `src/SkathIO.Rogue.Messaging` (bus, envelope, transport contract, in-memory
  transport, consumer), `src/SkathIO.Rogue.Messaging.RabbitMQ`, `src/SkathIO.Rogue.Messaging.Outbox`
  (outbox and inbox on the database job stores). Jobs and messaging have no generator of their own:
  the core generator emits their code through feature modules (`JobsModule.cs`, `MessagingModule.cs`).
- `tests/`: xunit. `Generator.Tests` drives the generator in-memory; `Smoke.*` is a deliberate
  multi-project solution (Infrastructure/Application/Api/Tests) that catches cross-assembly wiring bugs.
  `tests/consumer/` is not in `SkathIO.Rogue.sln`: it has its own build files and NuGet.config, and
  consumes the packed packages, never project references.
- `samples/`, `bench/` (BenchmarkDotNet; committed baselines in `bench/results/<date>-<sha>/`,
  allocation thresholds in `.bench/thresholds.json`), `docs/`, `site/`.
- Docs at `/rogue/docs/`: hashira builds them from `docs/*.md` as they are (no front matter or
  link edits) and `.github/pages.json` (sidebar labels, header links, `governance.md` and
  `roadmap.md` unpublished; `.github/pages-theme.css` keeps the light and dark brand colours).
  `tools/docs-api/` holds `docfx.json` and `clean-api.mjs`, which generate the API pages under
  `/rogue/docs/api/` from the `///` comments, and their tests. Nothing generated is committed; the
  docs packages live in hashira, so `.github/allowed-packages.txt` does not cover them.

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

- `.github/workflows/ci.yml` on every PR: `ci` (hashira's reusable `nuget-package-ci.yml`:
  build, `dotnet test` on the solution, scans, coverage, and SoMi's `quality-gate` on the PR's
  change at its default `strict` level), `Consumer suite`, `AOT publish`,
  `Benchmark smoke` (fails if the `ISender.Send` path allocates over the ceiling in
  `.bench/thresholds.json`), `License check`, `Public API`, `Docs build` (the full docs build;
  a dead link or a build error fails it).
- Every `skathio/hashira` ref in `ci.yml`, `publish.yml` (the `nuget-push` action and the two
  hashira checkouts) and `site.yml` (the Pages actions) tracks the moving `@v3` tag; keep them on
  the same ref and move them to `@v4` together when it ships. v3 has no `library_ref` input:
  passing it fails the run.
- The org's `ci-required` ruleset requires the `ci / passed` check (hashira's aggregate job) with
  the branch up to date, so the job that calls `nuget-package-ci.yml` must keep the id `ci`.
- Releases are manual: the maintainer dispatches `publish.yml` with a `bump`, and the release needs
  approval in the `production` environment. It packs, pushes to nuget.org over OIDC, tags, then
  deploys the site. Its `docs` job runs the docs build first, so a broken docs build stops the
  release before the push. Prep steps are in the `release-prep` skill. Agents never dispatch a release.
- `site.yml` calls hashira's `pages-site.yml@v3`, which copies `site/` to the root, adds the site
  kit in `_kit/`, builds the docs under `docs/` and deploys; only `publish.yml` may pass
  `released: true`. `homepage.yml` pings skathio.github.io when `.github/homepage.json`
  or `site/mark.svg` changes on main.

## Visual testing (required for anything that renders)

`site/` is the frontend: one static page (`index.html`, `mark.svg`) with no build step. It uses
hashira's site kit from `_kit/` (added by the Pages build): `tokens.css` for colours and light and
dark, `theme.js` for the toggle that sets `data-theme`, and the `<site-nav>` header from
`site-nav.js`, which carries the same name, logo, nav and `home_link` as `.github/pages.json` so
the landing and the docs show one header. Rogue's accent is `--site-accent` (light) and
`--site-accent-dark` on `:root`; the page never restyles the nav.
Any change that touches `site/`, `docs/`, `tools/docs-api/` or `.github/pages*`, or changes what the page shows, must be visually tested before
it is called done:

1. Run `tools/docs-api/build-pages.sh`, then `.claude/skills/visual-test/screenshot.sh <out-dir>`. It serves
   `site/`, the kit and the docs route on a free port and takes phone (390×844) and desktop
   (1280×800) screenshots in light and dark mode of the landing page, Getting started and the `ISender` API page.
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
