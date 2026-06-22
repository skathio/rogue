# SkathIO.Rogue — Benchmark Results

Measured comparison of SkathIO.Rogue against MediatR, plus Rogue-only scaling checks. Numbers come
from committed BenchmarkDotNet runs; the raw per-class artifacts live under `bench/results/<date>-<sha>/`.

## Methodology

- **Harness:** BenchmarkDotNet 0.15.2, ShortRun job (3 iterations, 3 warmup, 1 launch).
- **SDK / runtime:** .NET 10.0.109 SDK, .NET 10.0.9 runtime (X64 RyuJIT AVX2).
- **Hardware / OS:** Intel Core i7-6700HQ (Skylake), 4 physical / 8 logical cores · Ubuntu 24.04.4 LTS.
- **Competitor:** MediatR 12.4.1 (Apache-2.0). Comparison-only; not a runtime dependency of any package.
- **Raw artifacts:** `bench/results/2026-06-19-cb81a30/` (non-Publish scenarios) and
  `bench/results/2026-06-20-a734d6f/` (notification fan-out).
- **Reproduce:**

  ```bash
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*' --job short
  ```

> ShortRun is a fast, wide sweep with relatively large error bands; treat the means as indicative and
> the allocation counts (deterministic, from `[MemoryDiagnoser]`) as exact. Earlier pre-optimization
> baselines are preserved in git history and summarized in the [CHANGELOG](../CHANGELOG.md).

## Results — Rogue vs MediatR

Mean wall-clock and allocated bytes per operation. Lower is better. **Bold** marks the winner per row.

| # | Scenario | Rogue | MediatR | Verdict |
|---|----------|-------|---------|---------|
| 1 | NoBehavior `Send` (typed, 0 behaviors), `ISender` path | **93.96 ns** / **48 B** | 114.71 ns / 224 B | Rogue faster + fewer B |
| 1c | NoBehavior, generated concrete path (`SendPingRequest`) | **30.58 ns** / 48 B | n/a | Rogue-only fast lane |
| 2 | Cold-start (first dispatch incl. DI build) | **20.65 µs** / **25.65 KB** | 398.98 µs / 618.34 KB | **Rogue ~19×** (no runtime scan) |
| 3 | Object-path (untyped `Send(object)`, 1 handler) | **83.46 ns** / **48 B** | 120.98 ns / 296 B | Rogue faster + fewer B |
| 3b | Object-path at 25 handler types | 88.09 ns / 48 B | n/a | O(1) scaling (1 vs 25 identical) |
| 4 | Publish — 2 event handlers | **120.6 ns** / **112 B** | 209.9 ns / 464 B | Rogue faster + fewer B |
| 5 | Publish — 5 event handlers | **258.0 ns** / **208 B** | 409.6 ns / 920 B | Rogue faster + fewer B |
| 6 | Publish — 20 event handlers | **883.6 ns** / **688 B** | 1,459.2 ns / 3,200 B | Rogue faster + fewer B |
| 7 | CreateStream (10 items) | 376.9 ns / 344 B | n/a | Rogue-only (MediatR v12 core has no streaming) |

Across the currently-measured head-to-head scenarios, Rogue meets or beats MediatR on **both** wall-clock
and allocation, plus the structural ~19× cold-start lead.

## Allocation note

Rogue's typed dispatch is designed for a zero-allocation concrete path:

- The published `ISender.Send<T>` interface path boxes exactly one `ValueTask<T>` by design (**48 B**).
- The generated concrete path (`RogueDispatcher.Send{Request}`) adds **0 B** of its own — the residual
  48 B in row 1c is the consumer's choice to register the handler as `Transient` (a fresh instance per
  dispatch), not dispatch overhead. The 0-byte dispatch core is verified by a unit test asserting a
  0-byte per-operation delta over 1000 iterations via `GC.GetAllocatedBytesForCurrentThread`.

Do not read the published interface-path number as the concrete-path number.

## Where Rogue is not the fastest

We commit to documenting any realistic scenario where Rogue does **not** win. As of this baseline, there
is **no measured head-to-head scenario where MediatR beats Rogue** — including the notification fan-out
(N = 2 / 5 / 20), which an earlier release lost on time and which the fan-out rewrite closed on both
axes. If a future change (or a higher fan-out N) reintroduces a loss, it will be reported here in the
same plain terms.

## Rogue-only scaling checks

These have no MediatR equivalent and must not be read as comparisons.

**Pipeline-depth scaling.** Dispatching with N = 1 / 3 / 5 closed pipeline behaviors, latency is flat
across depth (no per-behavior heap growth) and allocation is flat at 872 B:

| Family | N=1 | N=3 | N=5 | Allocated |
|--------|----:|----:|----:|----------:|
| `ISender.Send` | 379.1 ns | 379.8 ns | 364.9 ns | 872 B |
| concrete path (`SendChainPingRequest`) | 366.5 ns | 338.0 ns | 349.4 ns | 872 B |

The 872 B is honest: the statically-typed behavior chain removes the per-call pipeline-state boxing, but
each chain link's forwarding lambda plus the transient handler/behavior instances still allocate.

**Concurrency.** Concurrent `Send` across N held DI scopes via `Task.WhenAll`
(`[ThreadingDiagnoser]` + `[MemoryDiagnoser]`):

| Concurrency | Mean | Allocated | Lock Contentions |
|------------:|-----:|----------:|-----------------:|
| 1  |    871.6 ns |  2.70 KB | 0 |
| 4  |  3,614.1 ns | 10.27 KB | 0 |
| 8  |  7,096.8 ns | 20.37 KB | 0 |
| 16 | 13,617.9 ns | 40.55 KB | 0 |

Latency and allocation scale linearly, with **zero lock contention** at every level — the generated
dispatcher holds no shared mutable state.
