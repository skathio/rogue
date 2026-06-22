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
  `Microsoft.Extensions.DependencyInjection.Abstractions` (plus netstandard2.0 polyfills). Integration
  packages add exactly one dependency each (`Microsoft.Extensions.Logging.Abstractions`,
  `FluentValidation`).
- **Transitive dependency allow-list.** The `license-check` CI job runs
  `dotnet list <project> package --include-transitive --format json` for each of the 5 packable
  projects (`SkathIO.Rogue.Abstractions`, `SkathIO.Rogue`, `SkathIO.Rogue.MediatR`,
  `SkathIO.Rogue.Logging`, `SkathIO.Rogue.Validation.FluentValidation`), unions the resulting
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
- **Allow-list scope is the full build-time restore graph, not just shipped `.nuspec`
  dependencies.** `dotnet list package --include-transitive` enumerates everything needed to
  restore and build each packable project, including `PrivateAssets="All"`/build-time-only
  tooling (e.g. MinVer, SourceLink, `Microsoft.CodeAnalysis.PublicApiAnalyzers`,
  `Microsoft.NET.ILLink.Tasks`) that never flows to a consumer's `.nuspec`. This is intentionally
  broader than "what a consumer of the published package pulls in" — adding a dev-only analyzer
  or build tool will also trip this gate and require an allow-list entry.

## Versioning and releases

- **Semantic versioning**, MinVer-driven from git tags. A `vMAJOR.MINOR.PATCH` tag is the
  version source of truth.
- **Public API is gated.** Each package tracks its surface in `PublicAPI.Shipped.txt`; the
  `public-api` CI job fails any unintended public-surface change. Surface that differs by target
  framework is tracked per-TFM.
- **Releases are tag-triggered.** Pushing a `v*` tag runs `.github/workflows/publish.yml`, which calls
  the `skathio/hashira` reusable `nuget-package-publish` workflow (pinned to a SHA) to pack and
  push all packages to NuGet.org atomically. The push is gated by the `production` GitHub Environment
  (required reviewer + `NUGET_API_KEY` secret). See [release readiness](#release-readiness).

## Supported target frameworks

`netstandard2.0`, `net8.0`, `net10.0`. netstandard2.0 keeps the contracts consumable from older
runtimes; net8.0/net10.0 get the streaming surface and the in-box `ValueTask`/diagnostics types.

## Quality bar

- `TreatWarningsAsErrors = true` across all target frameworks — zero warnings.
- AOT/trim clean: the AOT sample publishes with no IL trim or AOT warnings.
- Security NFRs: reflection-free dispatch; payload logging off by default.

## Release readiness

Before tagging `v1.0.0`:

- The repository **must be public**. Benchmarks (`bench/results/`), this roadmap, and the
  governance docs are only publicly accessible when the repo is public — a v1.0.0 tag on a private
  repo would ship packages whose linked documentation 404s for consumers. Confirm visibility in the
  GitHub repository settings before tagging. This is a release-process check, not an automated gate.
- The `production` GitHub Environment must have a required reviewer and the `NUGET_API_KEY` secret
  configured (consumer responsibility per the hashira nuget flow; an Environment with zero
  reviewers publishes without pausing).
- **Promote `PublicAPI.Unshipped.txt` entries to `PublicAPI.Shipped.txt`** for all packable projects
  (use the analyzer's "mark shipped" fixer; preserve the per-TFM split — `PublicAPI/netstandard2.0/`
  + `PublicAPI/modern/` — for `Abstractions`, `SkathIO.Rogue`, and `SkathIO.Rogue.MediatR`). This is
  the **one-way API freeze for v1**: once `Shipped` ships under the `v1.0.0` tag the surface is
  frozen, so this must happen before — or atomically with — the tag. It is irreversible and is the
  reason it lives on this checklist rather than in an automated gate (the `public-api` CI job only
  fails on *undeclared* surface, not on Unshipped-vs-Shipped placement).
- **Run the full benchmark suite and commit the baseline** to `bench/results/<date>-<sha>/` so the
  published competitive comparison is backed by committed data rather than placeholders. (Satisfied for
  v1.0.0: the committed baselines live under `bench/results/`, newest `2026-06-20-a734d6f/`.)

## Contributing

Issues and pull requests are welcome at <https://github.com/skathio/rogue>. Changes that alter public
API must update the relevant `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` (the `public-api` gate
enforces this) and the [CHANGELOG](../CHANGELOG.md).
