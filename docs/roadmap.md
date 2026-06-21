# Roadmap

This roadmap reflects work **actually deferred** during the v1 build (tracked in the project's
progress log), not speculative features. v1.0.0 is feature-complete against its requirements; the
items below are post-v1 candidates.

## v1.0.0 (shipped)

Source-generated, reflection-free CQRS/mediator. See the [CHANGELOG](../CHANGELOG.md) for the full
feature set: queries/commands (CQS-explicit core), events with fan-out, streaming queries, pipeline +
stream behaviors with ordering, pre/post processors and exception handlers, compile-time diagnostics
(ROGUE001–006 + ROGUE010/011/012; ROGUE007 unused), logging + FluentValidation integrations, a
telemetry shim, MediatR compatibility + migration analyzer (ROGM001–006), AOT/trim support, and a
benchmark suite against MediatR.

## Post-v1 candidates

**Performance**

- ~~**Allocation-free notification fan-out.**~~ **Done (`publish-fanout-perf`, 2026-06-20).** The
  goal — close the NFR-PERF-5 not-fastest Publish scenario — is achieved: Rogue now wins Publish N=5
  and N=20 against MediatR on both time and allocation (see
  [bench/RESULTS.md](../bench/RESULTS.md#post-publish-fanout-perf)). Delivered in two layers: the
  per-call `GetServices<>()` DI enumeration was already eliminated by `rogue-perf`'s D1/D1a
  cached-factory-array change; `publish-fanout-perf` then removed the remaining `EventHandlerExecutor`
  wrapper/closure/boxed-enumerator abstraction tax (D2 generic `IEventPublisher`) plus the per-call
  publisher re-resolution and unconditional async state machine (D3). The benchmark honesty entry is
  updated accordingly.
- **Singleton-lifetime notification-handler caching (new, filed 2026-06-20).** For event handlers
  registered at `ServiceLifetime.Singleton`, `Publish` still re-invokes a factory delegate
  (`GetRequiredService<THandler>()`) on every call even though a singleton handler's resolved
  instance never changes — the per-call indirection buys nothing. `publish-fanout-perf`'s Phase 2
  attempted this (tracked as D4) and discovered the chosen mechanism is architecturally impossible:
  the source generator has no compile-time visibility into the runtime `RogueOptions.Lifetime`
  value (`RogueEmitOptions` carries no such field, and nothing feeds one into generation — see
  `.somi/plans/publish-fanout-perf/decisions.md` D5 for the full discovery). Phase 2 was abandoned
  before any code was written. A future attempt needs to resolve this via one of: (a) a build-time
  config signal (e.g. an MSBuild property) fed into `RogueEmitOptions`, accepting a two-places-to-
  declare-lifetime DX cost; or (b) a new compile-time-visible per-handler marker (attribute or
  marker interface), accepting new public API surface. Either needs its own `/plan` pass before
  implementation — this is not a same-iteration fix.
- **Public concrete dispatch entry point.** The 0-byte concrete path is currently verified only by
  internal integration tests because the generated `Send_X` method is not reachable from a consumer
  benchmark. Emitting a public per-request dispatch entry point would let the published benchmark
  machine-verify the 0-byte claim end-to-end.
- **FrozenDictionary object dispatch.** The untyped `Send(object)` path uses a generated `switch`
  (measured O(1) to 25 handler types, PD-3a). Revisit a `FrozenDictionary` lookup only if real
  workloads with many more request types show the switch is inadequate.

**Generator correctness**

- **Nested-type FQN.** A handler/behavior declared as a nested type produces a containing-namespace
  FQN that drops the outer-type qualifier (worked around in benchmarks by hoisting to top level). Fix
  the FQN construction to include containing types, and add a generator test.
- **Multi-interface handler types.** A type implementing two Rogue handler interfaces is discovered as
  only one item today; the second registration is silently dropped. Emit multiple registrations or a
  diagnostic for dual-role types. *(Originally flagged as a pre-GA item in Phase 3.1; it ships in
  v1.0.0 as a documented known limitation because the fix requires a structural change to the
  one-item-per-node discovery pipeline and the failure mode — a silent registration drop on an
  uncommon dual-role type — is narrow. Deferred to post-v1 deliberately rather than dropped.)*

**Migration tooling**

- **ROGM004 invocation analyzer + code-fix.** The `AddMediatR(...)` → `AddRogue(...)` descriptor is
  defined and documented but no analyzer currently fires it (the runtime compat shim already forwards
  the call). Wire the invocation analyzer/code-fix if the migration UX needs it.
- **AC-F end-to-end fixture.** A ~50-handler sample with an automated "apply code-fix → output
  compiles" test (the before/after sample documents the transform today).

**Integration**

- **Opt-in for referenced-assembly open behaviors.** Auto-applying package-provided open behaviors to
  every request is surprising and forces the package's DI dependencies. Consider an opt-in gate for
  open behaviors discovered in referenced (vs. consumer-owned) assemblies.

## Out of scope

Competition packages (MediatR, `Mediator.*`) are never a runtime dependency of `src/`; they appear
only in benchmarks and migration tooling. There is no plan to add runtime reflection or assembly
scanning — compile-time generation is the design.
