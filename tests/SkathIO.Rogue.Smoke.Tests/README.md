# SkathIO.Rogue smoke test — layered multi-project solution

An end-to-end smoke test for the whole library, structured as a real layered solution rather than a
single assembly:

```
SkathIO.Rogue.Smoke.Api              (WebApplicationFactory host — zero handlers/behaviors of its own)
  -> ProjectReference: SkathIO.Rogue, SkathIO.Rogue.Logging
  -> ProjectReference: SkathIO.Rogue.Smoke.Application

SkathIO.Rogue.Smoke.Application      (all commands/queries/events/behaviors live here)
  -> ProjectReference: SkathIO.Rogue, SkathIO.Rogue.Validation.FluentValidation
  -> ProjectReference: SkathIO.Rogue.Smoke.Infrastructure

SkathIO.Rogue.Smoke.Infrastructure   (in-memory "persistence" — no Rogue reference at all)

SkathIO.Rogue.Smoke.Tests            (this project — references the Api host only)
```

This mirrors the exact shape [GitHub issue #21](https://github.com/skathio/rogue/issues/21)
described: a host project with no Rogue declarations of its own, reaching a behavior package only
*transitively* through a `ProjectReference` to a sibling project. `SkathIO.Rogue.Smoke.Api` never
references `SkathIO.Rogue.Validation.FluentValidation` directly — it only sees
`ValidationBehavior<,>` because `SkathIO.Rogue.Smoke.Application` does, and MSBuild/NuGet propagate
that reference transitively, same as a real consumer's `PackageReference` would. See
`.somi/plans/pd17-metadata-suppression/rca.md` for the defect this shape used to trigger.

## What it covers

`SmokeTests.cs` drives every core dispatch kind through real HTTP calls against the booted host:

| Behavior | Endpoint(s) |
|---|---|
| `ICommand<TResponse>` | `POST /orders` |
| `ICommand` (void) | `POST /orders/{id}/ship` |
| `IQuery<T>` | `GET /orders/{id}` |
| `IEvent` fan-out (two handlers) | `POST /orders` (publishes internally), asserted via `GET /_diagnostics/activity` |
| `IStreamQuery<T>` | `GET /orders/stream` |
| Custom open `IPipelineBehavior<,>` + `[BehaviorOrder]` | every request (`OrderAuditBehavior`), asserted via `/_diagnostics/activity` |
| FluentValidation `ValidationBehavior<,>` (auto-woven, PD-17) | `POST /orders` with an invalid payload → 400, handler never runs |

`/_diagnostics/activity` is a test-only endpoint exposing the shared `IOrderActivityLog` so internal
pipeline/handler effects (fan-out, behavior wrapping) can be asserted through the same HTTP boundary
as everything else, without reaching into DI from the test.

## Why this doesn't replace the generator-level regression test

`MultiProjectBehaviorSuppressionTests` (in `SkathIO.Rogue.Generator.Tests`) is the deterministic
regression guard for issue #21 — it asserts the generator's suppression decision directly. Module
initializer execution order across assemblies is an unspecified implementation detail, not a
documented contract, so a real multi-assembly run like this one is not *guaranteed* to flip if the
underlying defect regresses — even though, empirically, reverting the fix locally made every test in
this project fail consistently across repeated runs (the empty `Api` dispatcher reliably won the
race in this build). Treat this suite as a broad "does the whole layered solution actually work"
smoke test that also happens to catch this regression in practice, not as a substitute for the
targeted, order-independent generator test.

## CI

These projects are part of `SkathIO.Rogue.sln`, so they run automatically under the same `ci` job
(`dotnet test SkathIO.Rogue.sln`) as every other test project, on every pull request — see
`.github/workflows/ci.yml`.
