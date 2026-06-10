# SkathIO.Rogue — Benchmark Results

> Generated: 2026-06-09 · commit `b037bd9` · SDK: .NET 10.0.108 · Runtime: .NET 10.0.8 (X64 RyuJIT AVX2)
> CPU: Intel Core i7-6700HQ (Skylake), 4 physical / 8 logical cores · OS: Ubuntu 24.04.4 LTS
> BenchmarkDotNet 0.15.2 · ShortRun job (3 iterations, 3 warmup, 1 launch)
> Competitors: MediatR 12.4.1 (Apache-2.0) · martinothamar/Mediator 3.0.2 (MIT)
> Raw artifacts (JSON / CSV / GitHub-MD per class): `bench/results/2026-06-09-b037bd9/`
> Reproduce: `dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*' --job short`

## Summary

Mean wall-clock (ns) and allocated bytes per operation. Lower is better. **Bold** marks the
best (lowest) value in each row. martinothamar/Mediator's compile-time-monomorphized dispatch is
the fastest and lowest-allocating library in every head-to-head scenario; Rogue's structural win
is **cold-start** (no reflection-based assembly scan), where it is ~40× faster than MediatR.

| # | Scenario | Rogue | MediatR | martinothamar | Notes |
|---|----------|-------|---------|---------------|-------|
| 1 | NoBehavior (typed Send, 0 behaviors) | 246 ns / 384 B | 107 ns / 224 B | **18 ns / 24 B** | Interface path boxes one `ValueTask<T>` (PD-12); see concrete-path note below |
| 1 | Cold-start (first dispatch incl. DI build) | **14.2 µs / 27.4 KB** | 567 µs / 824 KB | 63 µs / 75.6 KB | Rogue wins: no runtime assembly scan (source-generated registration) |
| 2 | Send + 1 / 3 / 5 pipeline behaviors | 232 / 240 / 237 ns · 384 B (flat) | n/a | n/a | Rogue only; allocation flat across behavior depth (no per-behavior heap growth) |
| 3 | Object-path (untyped Send, 1 handler) | 225 ns / 384 B | 123 ns / 296 B | **22 ns / 24 B** | |
| 3b | Object-path at 25 handler types | 226 ns / 384 B | n/a | n/a | PD-3a evidence: generated switch scales O(1) — 1 vs 25 handlers identical |
| 4a | Publish N=2 notification handlers | 354 ns / 784 B | 212 ns / 464 B | **23 ns / 24 B** | |
| 4b | Publish N=5 notification handlers | 727 ns / 1936 B | 390 ns / 920 B | **43 ns / 24 B** | |
| 5 | CreateStream (10 items) | 415 ns / 344 B | n/a | **300 ns / 216 B** | MediatR v12 core has no streaming |
| 6 | Publish N=20 (honesty scenario, NFR-PERF-5) | 2445 ns / 7312 B | 1363 ns / 3200 B | **141 ns / 24 B** | **See A1 hypothesis below** |
| C | ConcurrentSend (8 scopes via `Task.WhenAll`) | 4.1 µs / 6.9 KB | n/a | n/a | Rogue-only; 0 lock contentions across N=1/4/8/16 (scales linearly) |

Full concurrency sweep (Rogue only, `[ThreadingDiagnoser]` + `[MemoryDiagnoser]`):

| Concurrency | Mean | Allocated | Lock Contentions |
|------------:|-----:|----------:|-----------------:|
| 1  |   617 ns |  1.02 KB | 0 |
| 4  | 2,110 ns |  3.52 KB | 0 |
| 8  | 4,145 ns |  6.87 KB | 0 |
| 16 | 8,180 ns | 13.55 KB | 0 |

Allocation and latency scale linearly with concurrency (one DI scope + one boxed `ValueTask<T>`
per dispatch), with **zero lock contention** — the generated dispatcher holds no shared mutable
state, so concurrent dispatch is contention-free.

## AC-G — "≥1 scenario where SkathIO.Rogue is not fastest"

This criterion is **over-satisfied** by the measured data, and honestly so: martinothamar/Mediator
is the fastest and lowest-allocating library in *every* head-to-head dispatch scenario (NoBehavior,
object-path, Publish N=2/5/20, CreateStream), and MediatR is faster than Rogue on the single-dispatch
hot path. **Rogue is not the fastest mediator.** Its measured advantage is **cold-start** (scenario
1, cold): Rogue's first dispatch is ~14 µs vs MediatR's ~567 µs and martinothamar's ~63 µs, because
Rogue's registration is source-generated and does no runtime assembly scan. The honesty entry below
documents the most pronounced not-fastest scenario; the table above shows it is far from the only one.

### NFR-PERF-5 Honesty (Hypothesis A1) — Notification fan-out (N=20)

**Hypothesis A1 (PD-24):** Rogue is not the fastest library in at least one realistic scenario;
specifically, martinothamar's monomorphized fan-out should win on allocation at N=20.

**A1 status: CONFIRMED — on both allocation and wall-clock.**

| Library | Publish N=20 mean | Publish N=20 allocated |
|---------|------------------:|-----------------------:|
| Rogue | 2,445 ns | 7,312 B |
| MediatR | 1,363 ns | 3,200 B |
| martinothamar | **141 ns** | **24 B** |

**Mechanism:** Rogue's generated `Publish_PingNotificationN20()` resolves handlers at runtime via
`serviceProvider.GetServices<INotificationHandler<PingNotificationN20>>()` — a DI list enumeration
that allocates a `List<NotificationHandlerExecutor>` (plus one closure per handler) on **every**
`Publish` call. martinothamar's `Mediator.SourceGenerator` instead bakes a compile-time
`NotificationHandlers<T>` fan-out for each notification type, so its `Publish` path performs **no
per-call DI lookup and no per-call list allocation** (24 B flat regardless of N). Rogue's allocation
grows with N (784 B → 1936 B → 7312 B for N=2 → 5 → 20); martinothamar's stays at 24 B. At N=20 the
runtime `GetServices<>()` + list-build cost is ~17× martinothamar's wall-clock and ~300× its
allocation.

This is a **realistic, idiomatic** scenario (PD-24 / Major #5): publishing a domain event to 20
registered handlers is ordinary fan-out, not a pathological config. The notification count N is
encoded as a distinct notification type with exactly N declared handlers (see
`bench/SkathIO.Rogue.Benchmarks/Shared/BenchmarkHandlers.cs`), which yields an identical N across all
three libraries (each library's generator/scanner auto-discovers every handler for a notification
type, so selective DI registration cannot control N for MediatR/martinothamar — one type per N is the
only honest parameterization).

If a future Rogue change ever made the fan-out allocation-free (e.g. a generator-emitted static
handler array per notification type, mirroring martinothamar), this scenario would need
re-evaluation — though martinothamar would still lead on the single-dispatch hot path, so AC-G's
not-fastest criterion would remain satisfied regardless.

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
