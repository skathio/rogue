# SkathIO.Rogue — Benchmark Results

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
