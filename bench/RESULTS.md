# SkathIO.Rogue — Benchmark Results

This is a pre-release library (no version has been tagged yet — see `CHANGELOG.md`). This file has two
sections:

1. **[Post-optimization](#post-optimization--rogue-perf)** — the current numbers after the `rogue-perf`
   warm-path overhaul (D1/D1a, D2, D3, D4, D5). **Read this first.** This is what ships as `v1.0.0`.
2. **[Pre-optimization baseline](#pre-optimization-baseline)** — the numbers from before the
   `rogue-perf` overhaul, kept verbatim as the "before" reference for the optimization work itself (not a
   prior release).

---

## Post-optimization — `rogue-perf`

> Generated: 2026-06-19 · commit `cb81a30` **+ uncommitted `rogue-perf` working tree** (Phases 1–5;
>   numbers measured on the working tree, pre-commit) · SDK: .NET 10.0.109 · Runtime: .NET 10.0.9 (X64
>   RyuJIT AVX2)
> CPU: Intel Core i7-6700HQ (Skylake), 4 physical / 8 logical cores · OS: Ubuntu 24.04.4 LTS
> BenchmarkDotNet 0.15.2 · ShortRun job (3 iterations, 3 warmup, 1 launch) — same config as the pre-optimization baseline
> Competitors: MediatR 12.4.1 (Apache-2.0)
> Raw artifacts: `bench/results/2026-06-19-cb81a30/`
> Reproduce: `dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*' --job short`

### What changed

- **D4** — the generated dispatch skips the per-`Send` `GetService<IReadOnlyList<IPipelineBehavior<…>>>()`
  lookup entirely for requests with no statically-discovered behaviors (the common case). Direct handler call.
- **D2** — `Mediator` now constructor-injects (and caches) `RogueDispatcher` instead of resolving it via
  `GetRequiredService<RogueDispatcher>()` on every dispatch. `Mediator`/`ISender`/`IPublisher` move from
  `Transient` to `Scoped` to match the dispatcher's scoped lifetime. (Activated because AC-1 missed by 3.7 ns
  on D4 alone under ShortRun — see the AC table and the [D2 statistical justification](#d2-statistical-justification)
  below for the tighter-CI evidence that confirms this was a real effect, not ShortRun noise.)
- **D1/D1a** — `Publish` caches per-event `Func<IEventHandler<T>>[]` factory arrays in the dispatcher
  constructor, eliminating the per-`Publish` `GetServices<IEventHandler<T>>()` DI enumeration.
- **D3** — a public, generated concrete-dispatch fast path: `RogueDispatcher.Send{Request}()` extension
  methods that bypass the `ISender` `ValueTask<T>` box.
- **D5** — statically-typed per-request behavior chains (`Send_X_Chain_N`) replace the
  `PipelineExecutor` struct-boxing fold for closed (per-request) behaviors.

### Acceptance criteria (measured)

All figures transcribed verbatim from `bench/results/2026-06-19-cb81a30/`. **7 of 8 pass; AC-6 fails — see
its note.**

| AC | Target | Measured | Verdict |
|----|--------|----------|---------|
| **AC-1** | `Rogue_NoBehavior` mean ≤ 110 ns | **93.96 ns** | ✅ PASS |
| **AC-2** | `Rogue_Publish_N2` allocated ≤ 480 B | **384 B** | ✅ PASS |
| **AC-3** | `Rogue_Publish_N5` allocated ≤ 950 B | **840 B** | ✅ PASS |
| **AC-4** | `Rogue_ColdStart` mean ≤ 30 µs | **20.65 µs** | ✅ PASS |
| **AC-5** | `PipelineExecutorTests.Execute_NoBehaviors_ZeroAllocations` passes | passes | ✅ PASS |
| **AC-6** | `Rogue_NoBehavior_Concrete` allocated = 0 B | **48 B** | ❌ FAIL — see note |
| **AC-7** | all tests pass; 0 build warnings (all TFMs) | 233 pass / 0 warn | ✅ PASS |
| **AC-8** | `bench/RESULTS.md` updated honestly | this section | ✅ PASS |

### D2 statistical justification

D2's binary-breaking `Mediator` ctor change was originally activated on a 3.7 ns AC-1 miss (113.7 ns vs.
the ≤110 ns target) measured under ShortRun (3 iterations, 3 warmup, 1 launch) — a job with too little
statistical power to distinguish a few nanoseconds (its own Error band on the post-D2 ShortRun figure is
±84 ns on a 94 ns mean). A `rogue-perf` Phase 5.1 review flagged this as an unverified signal (see
`.somi/reviews/rogue-perf/2026-06-19-iteration-5-1-pass1.md`). To resolve it, `NoBehaviorBenchmarks` was
re-run under **MediumRun** (15 iterations, 10 warmup, 2 launches — `--job medium`) both with D2 reverted
(temporarily, for this measurement only) and with D2 applied:

| State | `Rogue_NoBehavior` mean | Error (99.9% CI half-width) | `MediatR_NoBehavior` mean | Error |
|-------|------------------------:|-----------------------------:|---------------------------:|-------:|
| Pre-D2  | 113.35 ns | ±1.897 ns | 112.58 ns | ±4.076 ns |
| Post-D2 | **90.64 ns** | ±1.637 ns | 110.62 ns | ±2.765 ns |

Raw artifacts: `bench/results/2026-06-20-cb81a30-pre-d2-medium/`, `bench/results/2026-06-20-cb81a30-post-d2-medium/`.

This settles both open questions from the review:

- **The pre-D2 AC-1 miss was real, not noise.** Pre-D2, Rogue's CI is [111.45, 115.24] ns — entirely above
  the 110 ns target, with a tight ±1.9 ns error band. The miss reproduces under a job with ~5× the
  statistical power of the original ShortRun measurement.
- **Pre-D2, Rogue was already at statistical parity with MediatR**, not meaningfully behind it (113.35 ns
  vs. 112.58 ns, heavily overlapping CIs). D2 was not "fixing a problem MediatR didn't have" — the AC-1
  target was an internal goal, independent of MediatR's number.
- **Post-D2, Rogue is genuinely and significantly faster than MediatR.** The CIs no longer overlap:
  Rogue [89.0, 92.3] ns vs. MediatR [107.85, 113.38] ns. D2 moved Rogue from parity-with-MediatR to a
  reproducible ~18% lead, not a coin-flip.

**AC-6 honest note.** The concrete fast path (`Rogue_NoBehavior_Concrete`) measures **48 B / 30.58 ns**, not
0 B. The 48 B is **not** Rogue dispatch overhead — it is the cost of resolving the **transient**
`PingHandler` from DI on every call (`AddTransient` → a fresh handler instance + the container's resolution
bookkeeping per dispatch). The genuinely-0-B claim is about the *dispatch core* — `Send_PingRequest_Direct`
itself adds no allocation — and that is what `PipelineExecutorTests.Execute_NoBehaviors_ZeroAllocations`
(AC-5, ✅) proves, by measuring the pipeline with a pre-resolved handler. AC-6 was written assuming
"concrete path = 0 B end-to-end", but any end-to-end dispatch that resolves a transient handler allocates
that handler; the benchmark was deliberately **not** rewritten to a singleton handler to manufacture a 0 B
reading, which would have been metric-gaming rather than a measurement of the real path. Net: the
concrete path is the fastest Rogue dispatch (30.58 ns vs the 93.96 ns `ISender` path) and adds 0 B of its
own; the residual 48 B is the consumer's transient-handler choice.

### Summary table — Rogue vs MediatR (post-optimization)

Mean wall-clock and allocated bytes per operation. Lower is better. **Bold** marks the winner per row.
Same hardware/run as above.

| # | Scenario | Rogue | MediatR | Ratio (Rogue/MediatR) | Verdict |
|---|----------|-------|---------|----------------------|---------|
| 1 | NoBehavior (typed `Send`, 0 behaviors), `ISender` path | **93.96 ns** / 48 B | 114.71 ns / **224 B** | 0.82× time | **Rogue faster** (mean) |
| 1c | NoBehavior, generated concrete path (`SendPingRequest`) | **30.58 ns** / 48 B | n/a | — | Rogue-only fast lane |
| 2 | Cold-start (first dispatch incl. DI build) | **20.65 µs** / **25.65 KB** | 398.98 µs / 618.34 KB | 0.052× time | **Rogue wins ~19×** |
| 3 | Object-path (untyped `Send(object)`, 1 handler) | **83.46 ns** / **48 B** | 120.98 ns / 296 B | 0.69× time | **Rogue faster** |
| 3b | Object-path at 25 handler types | 88.09 ns / 48 B | n/a | — | O(1) scaling (1 vs 25 identical) |
| 4a | Publish N=2 notification handlers | **196.6 ns** / **384 B** | 221.1 ns / 464 B | 0.89× time | **Rogue faster + fewer B** |
| 4b | Publish N=5 notification handlers | 417.7 ns / **840 B** | **389.5 ns** / 920 B | 1.07× time | **Mixed**: MediatR faster, Rogue fewer B |
| 5 | CreateStream (10 items) | 376.9 ns / 344 B | n/a | — | Rogue-only (MediatR v12 core has no streaming) |
| 6 | Publish N=20 (NFR-PERF-5 honesty) | 1,677.4 ns / 3,120 B | **1,354.3 ns** / 3,200 B | 1.24× time | **MediatR faster** (Rogue marginally fewer B) |

### Where Rogue is still slower than MediatR (NFR-PERF-5 honesty — non-negotiable)

Rogue is **not** the fastest mediator in every scenario. Measured, honestly:

- **Publish N=5 (4b)** — Rogue **417.7 ns** vs MediatR **389.5 ns** (Rogue is **~7% slower** on mean), though
  Rogue allocates fewer bytes (840 B vs 920 B). D1/D1a closed most of the pre-`rogue-perf` gap (1936 B → 840 B; 729 ns →
  418 ns) but MediatR's per-call dispatch is still marginally faster at this fan-out.
- **Publish N=20 (6, the honesty scenario)** — Rogue **1,677.4 ns** vs MediatR **1,354.3 ns** (Rogue is
  **~24% slower** on mean). Allocation is now near-parity (3,120 B vs 3,200 B), a large improvement from
  the pre-`rogue-perf` baseline's 7,312 B, but MediatR's reflection-based fan-out remains faster per-call at high N.

Where Rogue **wins**: cold-start (~19× faster — its structural advantage, no runtime assembly scan),
NoBehavior `Send` (mean and the concrete fast path), object-path `Send`, and Publish N=2 (both mean and
allocation). The honest positioning shift from the pre-`rogue-perf` baseline: Rogue went from *slower than MediatR on every
warm-path scenario* to **faster on the single-handler Send paths and Publish N=2, competitive at N=5, and
still behind at N=20 fan-out** — plus the unchanged ~19× cold-start lead.

### Pipeline-depth scaling (Rogue-only — NO MediatR comparison)

`NBehaviorsBenchmarks` is an **internal Rogue scaling/allocation check, not a "vs MediatR" scenario** — it
has no MediatR equivalent and must not be read as a comparison. Both families dispatch the dedicated
`ChainPingRequest` (distinct from `PingRequest` so the closed chain behaviors do not contaminate the
zero-behavior scenarios above) with N = 1/3/5 **closed** behaviors, exercising the D5 static chain
(`Send_ChainPingRequest_Chain_N`). The two families differ only by entry point:

| Family | N=1 | N=3 | N=5 | Allocated (flat) | Entry point |
|--------|----:|----:|----:|-----------------:|-------------|
| `Rogue_{N}Behaviors` (`ISender.Send`) | 379.1 ns | 379.8 ns | 364.9 ns | 872 B | interface path (one `ValueTask<T>` box) |
| `Rogue_{N}Behaviors_Chain_Concrete` (`RogueDispatcher.SendChainPingRequest`) | 366.5 ns | 338.0 ns | 349.4 ns | 872 B | concrete path (no `ISender` box) |

Latency is **flat across depth 1→5** (no per-behavior heap growth) — the D5 chain is O(1) in allocation
regardless of behavior count. The 872 B is honest: D5 removes the `PipelineState` struct-boxing the pre-`rogue-perf`
`PipelineExecutor` fold incurred, but each chain link's `() => next(...)` forwarding lambda plus the
transient handler/behavior instances still allocate, so "0 B on the behavior chain" is aspirational, not
achieved — these benchmarks measure the real bytes. (Previously this file's chain benchmarks silently
measured the `PipelineExecutor` fold, not the D5 chain, because an open generic behavior in the same
compilation vetoed chain emission compilation-wide; that topology gap is resolved — see the `rogue-perf`
diary's Phase 5 plan-change entry.)

### Concurrency (Rogue-only)

`[ThreadingDiagnoser]` + `[MemoryDiagnoser]`, concurrent `Send` across N held DI scopes via `Task.WhenAll`:

| Concurrency | Mean | Allocated | Lock Contentions |
|------------:|-----:|----------:|-----------------:|
| 1  |    871.6 ns |  2.70 KB | 0 |
| 4  |  3,614.1 ns | 10.27 KB | 0 |
| 8  |  7,096.8 ns | 20.37 KB | 0 |
| 16 | 13,617.9 ns | 40.55 KB | 0 |

Latency and allocation scale linearly with concurrency, **zero lock contention** at every level (the
generated dispatcher holds no shared mutable state). Note: per-scope allocation rose vs the pre-`rogue-perf` baseline
(1.02 KB → 2.70 KB at C=1) — under D2 each scope now resolves a `Scoped` `Mediator` (+ its cached
dispatcher) once per scope; this benchmark creates a fresh scope per concurrent op, so it pays that
per-scope cost where the old transient path amortized differently. This is a benchmark-shape artifact of
one-scope-per-op, not a per-dispatch regression (the single-dispatch NoBehavior path *dropped* to 48 B).

---

## Pre-optimization baseline

> Generated: 2026-06-17 · commit `5c236ee` · SDK: .NET 10.0.109 · Runtime: .NET 10.0.9 (X64 RyuJIT AVX2)
> CPU: Intel Core i7-6700HQ (Skylake), 4 physical / 8 logical cores · OS: Ubuntu 24.04.4 LTS
> BenchmarkDotNet 0.15.2 · ShortRun job (3 iterations, 3 warmup, 1 launch)
> Competitors: MediatR 12.4.1 (Apache-2.0)
> Raw artifacts: `bench/results/2026-06-17-5c236ee/`
> Reproduce: `dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*' --job short`
> Prior run: 2026-06-14, commit `5c236ee` (post-D5 CQS-shaped types, included martinothamar — removed 2026-06-17; see PD-48).

## Summary

Mean wall-clock (ns) and allocated bytes per operation. Lower is better. **Bold** marks the best
(lowest) value in each row. MediatR is faster than Rogue on every warm-path dispatch scenario;
Rogue's structural advantage is **cold-start** (no runtime assembly scan), where it is ~16.6× faster
than MediatR.

| # | Scenario | Rogue | MediatR | Notes |
|---|----------|-------|---------|-------|
| 1 | NoBehavior (typed Send, 0 behaviors) | 233 ns / 384 B | **104 ns / 224 B** | Interface path boxes one `ValueTask<T>` (PD-12); see concrete-path note below |
| 1 | Cold-start (first dispatch incl. DI build) | **25.4 µs / 26.8 KB** | 421.4 µs / 627.7 KB | Rogue wins: no runtime assembly scan (source-generated registration); see variance note below |
| 2 | Send + 1 / 3 / 5 pipeline behaviors | 239 / 229 / 234 ns · 384 B (flat) | n/a | Rogue only; allocation flat across behavior depth (no per-behavior heap growth) |
| 3 | Object-path (untyped Send, 1 handler) | 250 ns / 384 B | **117 ns / 296 B** | |
| 3b | Object-path at 25 handler types | 210 ns / 384 B | n/a | PD-3a evidence: generated switch scales O(1) — 1 vs 25 handlers identical |
| 4a | Publish N=2 notification handlers | 317 ns / 784 B | **200 ns / 464 B** | |
| 4b | Publish N=5 notification handlers | 729 ns / 1936 B | **392 ns / 920 B** | |
| 5 | CreateStream (10 items) | 389 ns / 344 B | n/a | Rogue-only — MediatR v12 core has no streaming |
| 6 | Publish N=20 (honesty scenario, NFR-PERF-5) | 2370 ns / 7312 B | **1405 ns / 3200 B** | **See honesty note below** |
| C | ConcurrentSend (8 scopes via `Task.WhenAll`) | 4.3 µs / 6.9 KB | n/a | Rogue-only; 0 lock contentions across N=1/4/8/16 (scales linearly) |

Full concurrency sweep (Rogue only, `[ThreadingDiagnoser]` + `[MemoryDiagnoser]`):

| Concurrency | Mean | Allocated | Lock Contentions |
|------------:|-----:|----------:|-----------------:|
| 1  |   572 ns |  1.02 KB | 0 |
| 4  | 2,142 ns |  3.52 KB | 0 |
| 8  | 4,252 ns |  6.87 KB | 0 |
| 16 | 8,099 ns | 13.55 KB | 0 |

Allocation and latency scale linearly with concurrency (one DI scope + one boxed `ValueTask<T>`
per dispatch), with **zero lock contention** — the generated dispatcher holds no shared mutable
state, so concurrent dispatch is contention-free.

## AC-G — "≥1 scenario where SkathIO.Rogue is not fastest"

This criterion is **over-satisfied** by the measured data, and honestly so: MediatR is faster than
Rogue in *every* warm-path dispatch scenario (NoBehavior, object-path, Publish N=2/5/20) — 5 of the
7 measured head-to-head scenarios. **Rogue is not the fastest mediator.** Its measured advantage is
**cold-start** (scenario 1, cold): Rogue's first dispatch is ~25.4 µs vs MediatR's ~421.4 µs
(~16.6× faster), because Rogue's registration is source-generated and does no runtime assembly scan.
The honesty note below documents the most pronounced not-fastest scenario (N=20 fan-out); the table
above shows it is far from the only one.

**Cold-start variance note (PD-48):** the 2026-06-09 pre-D5 baseline (`IRequest`-shaped) measured
14.21 µs ± 0.25 µs (CV ~1.7%). Post-D5 measurements have higher run-to-run spread — plausibly the
extra adapter-mapping registration inside `AddRogue()`. Both this run (25.4 µs) and the prior
2026-06-14 run (27.6 µs) keep Rogue well ahead of MediatR (421–639 µs range across both runs).
Investigating the increased variance (e.g. a longer job to shrink the CI, or profiling
`AddRogue()`'s first-call cost) is tracked as a follow-up in `progress.md`, not a blocker for AC-G.

### Honesty (NFR-PERF-5) — Notification fan-out (N=20)

Rogue is **not** the fastest library: MediatR beats Rogue on every warm-path dispatch (NoBehavior,
object-path, Publish N=2/5/20). The N=20 fan-out is the most pronounced not-fastest scenario.

| Library | Publish N=20 mean | Publish N=20 allocated |
|---------|------------------:|-----------------------:|
| Rogue | 2,370 ns | 7,312 B |
| MediatR | **1,405 ns** | **3,200 B** |

**Mechanism:** both Rogue and MediatR resolve their notification handlers via a runtime DI
enumeration (`serviceProvider.GetServices<...>()`) on every `Publish` call, so both allocate a
per-call handler list. Rogue's allocation grows with N (784 B → 1936 B → 7312 B for N=2 → 5 → 20).
MediatR's reflection-based dispatch is faster per-call and lower-allocating than Rogue's generated
fan-out at N=20.

This is a **realistic, idiomatic** scenario (Major #5): publishing a domain event to 20 registered
handlers is ordinary fan-out, not a pathological config. The notification count N is encoded as a
distinct event type with exactly N declared handlers (see
`bench/SkathIO.Rogue.Benchmarks/Shared/BenchmarkHandlers.cs`), which yields an identical N across
both libraries (each library's scanner auto-discovers every handler for an event type, so selective
DI registration cannot control N for MediatR — one type per N is the only honest parameterization).

## Concrete 0-Alloc Path (AC-6.2 / PD-12)

The `scenario1_rogue_allocated_bytes: 0` in `.bench/thresholds.json` refers to the **concrete**
`RogueDispatcher.Send_PingRequest()` path (PD-12), which is `private` on `internal
RogueDispatcherImpl` and therefore not directly measurable from a consumer assembly. The published
`Rogue_NoBehavior` benchmark measures the public `ISender.Send<T>` interface path, which boxes one
`ValueTask<T>` struct by design and allocates exactly **384 B** (measured here; this is the real
source for the figure previously cited unsourced in the docs). The 0-byte concrete-path claim is
authoritatively gated by `PipelineExecutorTests.Execute_NoBehaviors_ZeroAllocations`, which asserts
a per-operation delta of 0 B over 1000 iterations via `GC.GetAllocatedBytesForCurrentThread`.

To machine-verify the 0-byte claim end-to-end from a consumer, the generator would need to expose a
`public` per-request dispatch entry point — tracked as a HIGH follow-up in `progress.md`.
