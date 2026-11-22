# Governance

## License

SkathIO.Rogue is licensed under the [MIT License](../LICENSE). Every package declares
`PackageLicenseExpression = MIT`. The benchmark suite references MediatR (Apache-2.0) as a
comparison-only dependency; it is not a runtime dependency of any shipped package.

## Dependency policy

- **No competition packages in `src/`.** MediatR and `Mediator.*` must never appear as a runtime
  dependency of the source packages. They are confined to `bench/` (comparison) and to the MediatR
  migration tooling (which targets MediatR code in the *consumer's* project, not at runtime). CI
  enforces this with a `license-check` grep gate over `src/`.
- **No runtime reflection or assembly scanning.** Handler discovery and wiring are compile-time only.
- **Minimal runtime dependencies.** The core depends only on
  `Microsoft.Extensions.DependencyInjection.Abstractions`. Integration
  packages add exactly one dependency each (`Microsoft.Extensions.Logging.Abstractions`,
  `FluentValidation`); `SkathIO.Rogue.Validation` adds none. `SkathIO.Rogue.Jobs` adds
  `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.Hosting.Abstractions`, the
  latter so the worker runs as a hosted service. `SkathIO.Rogue.Jobs.Postgres` adds `Npgsql`.
  `SkathIO.Rogue.Jobs.SqlServer` adds `Microsoft.Data.SqlClient` and its transitive tree.
  `SkathIO.Rogue.Messaging` adds `Microsoft.Extensions.Logging.Abstractions` and
  `Microsoft.Extensions.Hosting.Abstractions`, so the consumer runs as a hosted service.
  `SkathIO.Rogue.Messaging.RabbitMQ` adds `RabbitMQ.Client` (and its dependency
  `System.IO.Pipelines`). `SkathIO.Rogue.Messaging.Outbox` adds no package: it builds on
  `SkathIO.Rogue.Jobs` and `SkathIO.Rogue.Messaging`.
- **Transitive dependency allow-list.** The `license-check` CI job runs
  `dotnet list <project> package --include-transitive --format json` for each packable project
  (`SkathIO.Rogue.Abstractions`, `SkathIO.Rogue`, `SkathIO.Rogue.MediatR`, `SkathIO.Rogue.Logging`,
  `SkathIO.Rogue.Validation.FluentValidation`, `SkathIO.Rogue.Validation`, `SkathIO.Rogue.Jobs`,
  `SkathIO.Rogue.Jobs.Postgres`, `SkathIO.Rogue.Jobs.SqlServer`, `SkathIO.Rogue.Messaging`,
  `SkathIO.Rogue.Messaging.RabbitMQ`, `SkathIO.Rogue.Messaging.Outbox`;
  a new package is added to the job's list when it is created), unions the resulting
  top-level + transitive package ids across all target frameworks, and **fails the build** if any id
  is not listed in [`.github/allowed-packages.txt`](../.github/allowed-packages.txt). **Adding a new
  package — or upgrading an existing one to a version that pulls in a new transitive dependency —
  requires adding its package id to `.github/allowed-packages.txt`.** Before adding an id, you
  **must first verify that the package's license is permissive**: MIT, Apache-2.0, BSD-2-Clause,
  BSD-3-Clause, MS-PL, 0BSD, Unlicense, or the .NET-foundation/`MICROSOFT` family. Record the
  verification on the same line as a comment directly above the id, in the format
  `# <id> — <SPDX/license> — verified <date>`. The gate itself only checks package *identity* — the
  license check is this human step at allow-list-edit time, and the per-line annotation is what makes
  "CI fails if a non-permissive transitive dependency enters the core" literally true. A package whose
  license is **not** permissive must not be added to the allow-list, and therefore cannot ship in any
  packable project.
- **Documented license exceptions.** A package whose license is not permissive enters the
  allow-list only when the maintainer accepts it by name, and the exception is recorded here and in
  its allow-list note. Current exceptions:
  - `Microsoft.Data.SqlClient.SNI.runtime` (Microsoft Software License Terms, a proprietary
    redistributable license): the native network layer of `Microsoft.Data.SqlClient`, which no SQL
    Server client for .NET can do without. It reaches consumers only through
    `SkathIO.Rogue.Jobs.SqlServer`; every other package stays free of it. Accepted by the
    maintainer on 2026-11-20.
- **Bench-only baselines.** The jobs bench (`bench/SkathIO.Rogue.Benchmarks.Jobs`) measures Rogue's
  jobs against Hangfire, accepted by the maintainer on 2026-11-20 for bench use only. These packages
  are not on the allow-list and must never enter a packable project's restore graph, or any project
  under `src/`, `tests/` or `samples/`; `JobsBenchRulesTests` checks both. As nuget.org reports them,
  verified 2026-11-20:
  - `Hangfire.InMemory` 1.0.0, `Hangfire.SqlServer` 1.8.25 and their dependency `Hangfire.Core`
    1.8.25: LGPL-3.0 (or a Hangfire commercial license), from the `LICENSE.md` in each package.
  - `Newtonsoft.Json` 13.0.4: MIT. Referenced directly to lift `Hangfire.Core`'s 11.0.1, which has a
    known high severity advisory.
- **Allow-list scope is the full build-time restore graph, not just shipped `.nuspec`
  dependencies.** `dotnet list package --include-transitive` enumerates everything needed to
  restore and build each packable project, including `PrivateAssets="All"`/build-time-only
  tooling (e.g. MinVer, SourceLink, `Microsoft.CodeAnalysis.PublicApiAnalyzers`,
  `Microsoft.NET.ILLink.Tasks`) that never flows to a consumer's `.nuspec`. This is intentionally
  broader than "what a consumer of the published package pulls in" — adding a dev-only analyzer
  or build tool will also trip this gate and require an allow-list entry.

## Versioning and releases

- **Semantic versioning**, explicitly resolved from git tags + a chosen bump (not inferred from
  commit messages). A `vMAJOR.MINOR.PATCH` tag is the version source of truth.
- **Public API is gated.** Each package tracks its surface in `PublicAPI.Shipped.txt`; the
  `public-api` CI job fails any unintended public-surface change. Every runtime package exposes the
  same surface on each target framework, so each tracks it in one `PublicAPI.Shipped.txt` /
  `PublicAPI.Unshipped.txt` pair at the project root.
- **Releases are dispatch-triggered.** A maintainer runs `.github/workflows/publish.yml` via
  `workflow_dispatch` and picks a `bump` (`patch`/`minor`/`major`). The `version` job resolves the
  next version (`skathio/hashira`'s `version-resolver` composite action), `dotnet-pack-version`
  packs with that version stamped in (MinVer's tag-inference is bypassed via its
  `MinVerVersionOverride` escape hatch), and `nuget-push` publishes to NuGet.org via OIDC trusted
  publishing, then creates the git tag + GitHub Release. There is no `nuget-package-publish`
  reusable workflow anymore — `skathio/hashira` retired it; these are composite actions invoked
  directly from this repo's own `publish.yml`, which is what makes the OIDC token's
  `job_workflow_ref` claim match `skathio/rogue/.github/workflows/publish.yml` (the policy this
  repo's nuget.org trusted-publishing config is scoped to). The push is gated by the `production`
  GitHub Environment (required reviewer). See [release readiness](#release-readiness).

## Supported target frameworks

Runtime packages target `net8.0` and `net10.0` only. `netstandard2.0` was dropped in 2.0.0, so .NET
Framework and netstandard2.0 libraries can no longer reference them; stay on 1.x for those. The
source generators and the migration analyzer still target `netstandard2.0`, because the compiler
loads analyzers only from that target.

## Quality bar

- `TreatWarningsAsErrors = true` across all target frameworks — zero warnings.
- AOT/trim clean: the AOT samples (`samples/SkathIO.Rogue.Aot.Sample`, and
  `samples/SkathIO.Rogue.Jobs.Messaging.Aot.Sample` for the jobs and messaging families) publish
  with no IL trim or AOT warnings.
- Security NFRs: reflection-free dispatch; payload logging off by default.

## Release readiness

Before dispatching **any** release (`publish.yml` via `workflow_dispatch`):

- **Promote `PublicAPI.Unshipped.txt` entries to `PublicAPI.Shipped.txt`** for any packable project
  that gained new public surface since the last release (use the analyzer's "mark shipped" fixer).
  This is the **one-way API freeze**:
  once `Shipped` ships under a tag, that surface is frozen, so this must happen before — or
  atomically with — the tag. It is irreversible and is the reason it lives on this checklist rather
  than in an automated gate (the `public-api` CI job only fails on *undeclared* surface, not on
  Unshipped-vs-Shipped placement). If no new public members were added, there's nothing to promote.
- **Run the full benchmark suite and commit a fresh baseline** to `bench/results/<date>-<sha>/` so
  the published competitive comparison is backed by committed data rather than placeholders, and
  update `bench/RESULTS.md`/`docs/benchmarks.md` if the numbers or covered scenarios changed.
  (Newest as of 1.1.0: `bench/results/2026-07-13-1bb0b86/`, including the validation-behavior
  comparison added that release.)
- **Update `CHANGELOG.md`**: move `[Unreleased]` content into a new dated `[MAJOR.MINOR.PATCH]`
  section matching the `bump` you're about to dispatch.
- **Verify the CHANGELOG's dependency/compatibility claims actually match what's on the branch
  being tagged, not just what's on `main` at some earlier point.** If a fix landed via more than
  one PR (e.g. a feature PR plus a follow-up hotfix for something a later, unrelated Dependabot
  merge regressed), confirm the follow-up is merged *before* tagging — check the specific package
  pins the CHANGELOG cites (`grep` `Directory.Packages.props` for the exact versions named) against
  the commit being released, not against the CHANGELOG text alone. A CHANGELOG that states a
  min-SDK/compatibility floor that isn't actually what ships is worse than no claim at all. (This
  happened once: 1.1.0's Roslyn-floor claim briefly depended on a hotfix PR that hadn't merged yet
  when the release notes were drafted — caught in review before tagging, not after.)
- The `production` GitHub Environment must have a required reviewer configured (an Environment
  with zero reviewers publishes without pausing) — a standing repo-settings requirement, checked
  once and not expected to change, but worth a glance if release cadence or team membership shifts.
  No `NUGET_API_KEY` secret is needed or set — this repo publishes via OIDC trusted publishing only
  (`auth: oidc`, fails loud rather than falling back to a token, and there is no token configured to
  fall back to). nuget.org's trusted-publishing policy must be scoped to `skathio/rogue`, workflow
  `publish.yml`, environment `production` (consumer responsibility per the hashira nuget flow).

The following was a one-time check for the initial `v1.0.0` launch and doesn't recur on later
releases, but is recorded here for history:

- The repository had to **be public** before v1.0.0 — benchmarks (`bench/results/`), the roadmap,
  and the governance docs are only publicly accessible once the repo is public, and a release on a
  private repo would ship packages whose linked documentation 404s for consumers.

## Contributing

Issues and pull requests are welcome at <https://github.com/skathio/rogue>. Changes that alter public
API must update the relevant `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` (the `public-api` gate
enforces this) and the [CHANGELOG](../CHANGELOG.md).
