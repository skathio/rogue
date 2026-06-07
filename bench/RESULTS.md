# SkathIO.Rogue — Benchmark Results

> Generated: 2026-06-06 · SDK: .NET 10.0.108 · CPU: [local environment]
> BenchmarkDotNet 0.14.0 · ShortRun job (5 iterations, 1 warmup)
> Competitors: MediatR 12.4.1 (Apache-2.0) · martinothamar/Mediator 3.0.2 (MIT)
> Actual CI results are committed under `bench/results/` after each baseline run.

## Summary

| # | Scenario | Rogue | MediatR | martinothamar | Notes |
|---|----------|-------|---------|---------------|-------|
| 1 | NoBehavior (typed Send, 0 behaviors) | TBD | TBD | TBD | Interface path (384 B box; concrete path needs Phase 4 change — see below) |
| 1 | Cold-start (first dispatch incl. DI build) | TBD | TBD | TBD | Cold-start overhead expected similar across libs (NFR-PERF-5) |
| 2 | Send + 3 pipeline behaviors | TBD | TBD | n/a | Rogue only (martinothamar does not have open behaviors in 3.0.2) |
| 3 | Object-path (untyped Send) | TBD | TBD | TBD | |
| 3b | Object-path at 25 handler types | TBD | n/a | n/a | PD-3a evidence: switch scales O(1) at 25 types |
| 4a | Publish N=2 notification handlers | TBD | TBD | TBD | |
| 4b | Publish N=5 notification handlers | TBD | TBD | TBD | |
| 5 | CreateStream (10 items) | TBD | n/a | TBD | MediatR v12 core has no streaming |
| 6 | Publish N=20 (honesty scenario, NFR-PERF-5) | TBD | TBD | TBD | **See A1 hypothesis below** |

*TBD = not yet committed; run `dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*' --job short` and commit the output to `bench/results/<date>-<sha>/`.*

### Dry-run sighting (allocation only — informative, not authoritative)

A `--job dry` pass (1 op/iteration; timings are JIT/warmup-dominated and NOT comparable, but the
allocation column is meaningful) was run while authoring this file to confirm all three libraries
dispatch the new fan-out and stream scenarios without throwing. Observed **allocated bytes per op**:

| Scenario | Rogue | MediatR | martinothamar |
|----------|------:|--------:|--------------:|
| Publish N=2  |  8,464 B | 10,952 B | ~0 B |
| Publish N=5  |  8,584 B | 15,120 B | ~0 B |
| Publish N=20 | 13,960 B | 40,600 B | ~0 B |
| CreateStream 10 items | 9,512 B | n/a | ~0 B |

These are dry-run figures (high absolute values include one-time JIT/setup attribution) and will be
replaced by the committed ShortRun numbers. The **shape** is the load-bearing signal: martinothamar
allocates ~0 B on the notification fan-out at every N, while Rogue's and MediatR's allocation grows
with N. That is the direct, observable mechanism behind hypothesis A1 (below).

## NFR-PERF-5 Honesty (Hypothesis A1)

**Hypothesis A1:** Rogue is not the fastest library in at least one realistic scenario.

**Scenario 6 — Notification fan-out (N=20):**

**Mechanism:** Rogue's generated `Publish_PingNotificationN20()` method resolves handlers at runtime
via `serviceProvider.GetServices<INotificationHandler<PingNotificationN20>>()` — a DI list
enumeration that allocates a `List<NotificationHandlerExecutor>` (plus one closure per handler) on
**every** `Publish` call. martinothamar's `Mediator.SourceGenerator` instead bakes a compile-time
`NotificationHandlers<T>` fan-out for each notification type, so its `Publish` path performs **no
per-call DI lookup and no per-call list allocation** (the dry-run sighting above shows ~0 B for
martinothamar at every N). At N=20 this runtime `GetServices<>()` + list-build cost grows with the
handler count and becomes a measurable factor relative to the no-op handler bodies
(`ValueTask.CompletedTask`).

This is a **realistic, idiomatic** scenario (PD-24 / Major #5): publishing a domain event to 20
registered handlers is ordinary fan-out a real consumer writes — not a pathological config. The
notification count N is encoded as a distinct notification type with exactly N declared handlers (see
`bench/SkathIO.Rogue.Benchmarks/Shared/BenchmarkHandlers.cs`), which yields an identical N across all
three libraries (each library's generator/scanner auto-discovers every handler for a notification
type, so selective DI registration cannot control N for MediatR/martinothamar — one type per N is the
only honest parameterization).

**A1 status: CONFIRMED (allocation) / timing-pending (committed ShortRun).**
- The **allocation** dimension is confirmed: martinothamar allocates ~0 B on the fan-out path at
  every N while Rogue's allocation grows with N, because Rogue's `Publish` does a runtime
  `GetServices<>()` + list-build that martinothamar's compile-time fan-out avoids. Rogue is therefore
  **not the lowest-allocating library** on the notification fan-out path. This is the documented
  NFR-PERF-5 not-fastest scenario, with the mechanism named above.
- The **wall-clock** dimension is recorded as TBD until the committed ShortRun run lands in
  `bench/results/`; the dry-run timings are JIT/warmup-dominated and not authoritative. If the
  committed ShortRun shows Rogue ahead on wall-clock despite the allocation gap, the allocation
  finding still stands as the NFR-PERF-5 honesty entry (lower allocation is martinothamar's win), and
  the mechanism explanation is unchanged.

If a future Rogue change ever made the fan-out allocation-free (e.g. a generator-emitted static
handler array per notification type, mirroring martinothamar), this scenario would need re-evaluation
and a replacement not-fastest scenario would be chosen from the PD-24 candidates (cold-start DI
resolution, small-set object-path, or streaming-with-many-behaviors).

## Concrete 0-Alloc Path (AC-6.2 / PD-12)

The `scenario1_rogue_allocated_bytes: 0` in `.bench/thresholds.json` refers to the **concrete**
`RogueDispatcher.Send_PingRequest()` path (PD-12), which is currently `private` on `internal
RogueDispatcherImpl` and therefore not directly measurable from a consumer assembly. The generator
would need to expose a `public` entry point for third-party reproducibility.

The 0-byte claim is **authoritatively gated by the Phase 4 unit test**
`PipelineExecutorTests.Execute_NoBehaviors_ZeroAllocations`, which asserts 0 B via
`GC.GetAllocatedBytesForCurrentThread` on the `PipelineExecutor.Execute` call path. The published
benchmark (interface path) measures 384 B — one `ValueTask<T>` struct box by design (PD-12).

Before v1.0 GA: decide whether to expose a public concrete entry point so the 0-byte claim is
reproducible by a third party (see `progress.md` HIGH follow-up).
