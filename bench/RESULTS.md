# SkathIO.Rogue — Benchmark Results

Measured comparison of SkathIO.Rogue against MediatR, plus Rogue-only scaling checks. Numbers come
from committed BenchmarkDotNet runs; the raw per-class artifacts live under `bench/results/<date>-<sha>/`.

## Methodology

- **Harness:** BenchmarkDotNet 0.15.8, ShortRun job (3 iterations, 3 warmup, 1 launch).
- **SDK / runtime:** .NET 10.0.109 SDK, .NET 10.0.9 runtime (X64 RyuJIT AVX2).
- **Hardware / OS:** Intel Core i7-6700HQ (Skylake), 4 physical / 8 logical cores · Ubuntu 24.04.4 LTS.
- **Competitor:** MediatR 12.5.0 (Apache-2.0). Comparison-only; not a runtime dependency of any
  package. Held below v13 deliberately (MediatR 13+ moved to a commercial license) — see
  [governance](../docs/governance.md#dependency-policy).
- **Raw artifacts:** `bench/results/2026-07-13-1bb0b86/` (full suite, including the validation
  comparison below). Earlier baselines (`2026-06-19-cb81a30`, `2026-06-20-a734d6f`, etc.) are
  retained for history.
- **Reproduce:**

  ```bash
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*' --job short
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks.Validation -- --filter '*' --job short
  ```

  The validation comparison lives in a separate project deliberately — referencing
  `SkathIO.Rogue.Validation.FluentValidation` from the shared benchmark project would auto-weave
  `ValidationBehavior<,>` into every request in that compilation (the same open-generic-behavior
  scan documented in the [behaviors guide](../docs/behaviors.md#auto-discovery-from-referenced-assemblies)),
  silently perturbing the zero-alloc benchmarks above.

> ShortRun is a fast, wide sweep with relatively large error bands; treat the means as indicative and
> the allocation counts (deterministic, from `[MemoryDiagnoser]`) as exact. Earlier pre-optimization
> baselines are preserved in git history and summarized in the [CHANGELOG](../CHANGELOG.md).

## Results — Rogue vs MediatR

Mean wall-clock and allocated bytes per operation. Lower is better. **Bold** marks the winner per row.

| # | Scenario | Rogue | MediatR | Verdict |
|---|----------|-------|---------|---------|
| 1 | NoBehavior `Send` (typed, 0 behaviors), `ISender` path | **91.53 ns** / **48 B** | 105.10 ns / 224 B | Rogue faster + fewer B |
| 1c | NoBehavior, generated concrete path (`SendPingRequest`) | **32.51 ns** / 48 B | n/a | Rogue-only fast lane |
| 2 | Cold-start (first dispatch incl. DI build) | **22.50 µs** / **26.17 KB** | 416.68 µs / 611.52 KB | **Rogue ~18.5×** (no runtime scan) |
| 3 | Object-path (untyped `Send(object)`, 1 handler) | **80.40 ns** / **48 B** | 125.52 ns / 296 B | Rogue faster + fewer B |
| 3b | Object-path at 25 handler types | 79.96 ns / 48 B | n/a | O(1) scaling (1 vs 25 identical) |
| 4 | Publish — 2 event handlers | **129.9 ns** / **112 B** | 218.9 ns / 464 B | Rogue faster + fewer B |
| 5 | Publish — 5 event handlers | **257.1 ns** / **208 B** | 404.8 ns / 920 B | Rogue faster + fewer B |
| 6 | Publish — 20 event handlers | **1,054.5 ns** / **688 B** | 1,389.2 ns / 3,200 B | Rogue faster + fewer B |
| 7 | CreateStream (10 items) | 386.6 ns / 344 B | n/a | Rogue-only (MediatR v12 core has no streaming) |
| 8 | **NEW** — Validated `Send` (real `FluentValidation` rule, valid payload, steady-state) | **549.4 ns** / **1.13 KB** | 579.0 ns / 1.27 KB | Rogue faster + fewer B |

Across the currently-measured head-to-head scenarios, Rogue meets or beats MediatR on **both** wall-clock
and allocation, plus the structural ~18.5× cold-start lead.

Row 8 is new in this baseline: a head-to-head with a real `FluentValidation` behavior in the
pipeline on both sides (Rogue's `ValidationBehavior<,>` vs. a hand-rolled MediatR
`IPipelineBehavior<,>` using the same validator and rule, matching semantics as closely as possible
— MediatR has no built-in validation). This is the scenario 1.1.0's DI-lifetime fix is about: a
pipeline behavior with a Scoped dependency (`IValidator<T>`), exercised at steady state with a
valid payload (the throwing/invalid path is deliberately not benchmarked —
exception-path costs are not representative under BenchmarkDotNet).

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
| `ISender.Send` | 374.9 ns | 362.0 ns | 385.8 ns | 872 B |
| concrete path (`SendChainPingRequest`) | 353.5 ns | 357.8 ns | 345.7 ns | 872 B |

The 872 B is honest: the statically-typed behavior chain removes the per-call pipeline-state boxing, but
each chain link's forwarding lambda plus the transient handler/behavior instances still allocate.

**Concurrency.** Concurrent `Send` across N held DI scopes via `Task.WhenAll`
(`[ThreadingDiagnoser]` + `[MemoryDiagnoser]`):

| Concurrency | Mean | Allocated | Lock Contentions |
|------------:|-----:|----------:|-----------------:|
| 1  |    970.4 ns |  2.71 KB | 0 |
| 4  |  3,625.3 ns | 10.30 KB | 0 |
| 8  |  7,143.0 ns | 20.43 KB | 0 |
| 16 | 14,323.5 ns | 40.68 KB | 0 |

Latency and allocation scale linearly, with **zero lock contention** at every level — the generated
dispatcher holds no shared mutable state.
