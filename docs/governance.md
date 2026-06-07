# Governance

## License

SkathIO.Rogue is licensed under the [MIT License](../LICENSE). Every package declares
`PackageLicenseExpression = MIT`. The benchmark suite references MediatR (Apache-2.0) and
martinothamar/Mediator (MIT) as comparison-only dependencies; neither is a runtime dependency of any
shipped package.

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

## Versioning and releases

- **Semantic versioning**, MinVer-driven from git tags (PD-7). A `vMAJOR.MINOR.PATCH` tag is the
  version source of truth.
- **Public API is gated.** Each package tracks its surface in `PublicAPI.Shipped.txt`; the
  `public-api` CI job fails any unintended public-surface change. Surface that differs by target
  framework is tracked per-TFM.
- **Releases are tag-triggered.** Pushing a `v*` tag runs `.github/workflows/publish.yml`, which calls
  the `skathio/hashira-ops` reusable `nuget-package-publish` workflow (pinned to a SHA) to pack and
  push all packages to NuGet.org atomically. The push is gated by the `production` GitHub Environment
  (required reviewer + `NUGET_API_KEY` secret). See [release readiness](#release-readiness).

## Supported target frameworks

`netstandard2.0`, `net8.0`, `net10.0`. netstandard2.0 keeps the contracts consumable from older
runtimes; net8.0/net10.0 get the streaming surface and the in-box `ValueTask`/diagnostics types.

## Quality bar

- `TreatWarningsAsErrors = true` across all target frameworks — zero warnings.
- AOT/trim clean: the AOT sample publishes with no IL trim or AOT warnings.
- Security NFRs (NFR-SEC-1…4): reflection-free dispatch; payload logging off by default.

## Release readiness

Before tagging `v1.0.0`:

- The repository **must be public** (NFR-LIC-2). Benchmarks (`bench/results/`), this roadmap, and the
  governance docs are only publicly accessible when the repo is public — a v1.0.0 tag on a private
  repo would ship packages whose linked documentation 404s for consumers. Confirm visibility in the
  GitHub repository settings before tagging. This is a release-process check, not an automated gate.
- The `production` GitHub Environment must have a required reviewer and the `NUGET_API_KEY` secret
  configured (consumer responsibility per the hashira-ops nuget flow; an Environment with zero
  reviewers publishes without pausing).
- **Promote `PublicAPI.Unshipped.txt` entries to `PublicAPI.Shipped.txt`** for all packable projects
  (use the analyzer's "mark shipped" fixer; preserve the per-TFM split — `PublicAPI/netstandard2.0/`
  + `PublicAPI/modern/` — for `Abstractions`, `SkathIO.Rogue`, and `SkathIO.Rogue.MediatR`). This is
  the **one-way API freeze for v1**: once `Shipped` ships under the `v1.0.0` tag the surface is
  frozen, so this must happen before — or atomically with — the tag. It is irreversible and is the
  reason it lives on this checklist rather than in an automated gate (the `public-api` CI job only
  fails on *undeclared* surface, not on Unshipped-vs-Shipped placement).
- **Run the full benchmark suite and commit the baseline** to `bench/results/<date>-<sha>/`, then
  populate the `TBD` cells in `bench/RESULTS.md` so the published competitive comparison is backed by
  committed data rather than placeholders.

## Contributing

Issues and pull requests are welcome at <https://github.com/skathio/rogue>. Changes that alter public
API must update the relevant `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` (the `public-api` gate
enforces this) and the [CHANGELOG](../CHANGELOG.md).
