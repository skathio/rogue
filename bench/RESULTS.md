# SkathIO.Rogue — Benchmark Results

Measured comparison of SkathIO.Rogue against MediatR, plus Rogue-only scaling checks. Numbers come
from committed BenchmarkDotNet runs; the raw per-class artifacts live under `bench/results/<date>-<sha>/`.

## Methodology

- **Harness:** BenchmarkDotNet 0.15.8, default job (BenchmarkDotNet picks the warmup and iteration
  counts until the result is stable; no `--job short`).
- **SDK / runtime:** .NET 10.0.115 SDK, .NET 10.0.15 runtime (X64 RyuJIT x86-64-v3).
- **Hardware / OS:** a laptop, an Intel Core i7-6700HQ (Skylake, 2.60 GHz, 4 physical / 8 logical
  cores), Ubuntu 24.04.5 LTS. Its Docker data, and so the SQL Server database used by the jobs
  benchmarks, sit on a spinning disk (7,200 rpm), not an SSD. BenchmarkDotNet could not raise its
  process priority (no permission), so other work on the laptop can show in the times.
- **Competitor:** MediatR 12.5.0 (Apache-2.0). Comparison-only; not a runtime dependency of any
  package. Held below v13 deliberately (MediatR 13+ moved to a commercial license) — see
  [governance](../docs/governance.md#dependency-policy).
- **Raw artifacts:** `bench/results/2026-11-22-696836e/`, the 2.0.0 baseline: a full (non-ShortRun)
  run of every class in `bench/SkathIO.Rogue.Benchmarks` and `bench/SkathIO.Rogue.Benchmarks.Validation`
  at commit `696836e`. Earlier baselines (`2026-07-13-1bb0b86`, a ShortRun of the same suite, then
  `2026-06-19-cb81a30`, `2026-06-20-a734d6f`, etc.) are retained for history.
  The jobs benchmarks were run on 2026-11-21 with their own setup and were not rerun; see
  [Jobs and validation](#jobs-and-validation) below.
- **Reproduce:**

  ```bash
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*'
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks.Validation -- --filter '*'
  ```

  The full core suite took 12 minutes on this laptop and the validation suite 4. Add `--job short`
  for a quicker, noisier sweep.

  The validation comparison lives in a separate project deliberately — referencing
  `SkathIO.Rogue.Validation.FluentValidation` from the shared benchmark project would auto-weave
  `ValidationBehavior<,>` into every request in that compilation (the same open-generic-behavior
  scan documented in the [behaviors guide](../docs/behaviors.md#auto-discovery-from-referenced-assemblies)),
  silently perturbing the zero-alloc benchmarks above.

> The error bands of this full run are a few percent of the mean; the earlier ShortRun baselines had
> much wider ones. Allocation counts (deterministic, from `[MemoryDiagnoser]`) are exact either way.
> Earlier pre-optimization baselines are preserved in git history and summarized in the
> [CHANGELOG](../CHANGELOG.md).

## Results — Rogue vs MediatR

Mean wall-clock and allocated bytes per operation. Lower is better. **Bold** marks the winner per row.

| # | Scenario | Rogue | MediatR | Verdict |
|---|----------|-------|---------|---------|
| 1 | NoBehavior `Send` (typed, 0 behaviors), `ISender` path | **84.07 ns** / **48 B** | 98.89 ns / 224 B | Rogue faster + fewer B |
| 1c | NoBehavior, generated concrete path (`SendPingRequest`) | **27.03 ns** / 48 B | n/a | Rogue-only fast lane |
| 2 | Cold-start (first dispatch incl. DI build) | **20.02 µs** / **26.17 KB** | 414.24 µs / 611.51 KB | **Rogue ~20.7×** (no runtime scan) |
| 3 | Object-path (untyped `Send(object)`, 1 handler) | **72.29 ns** / **48 B** | 114.64 ns / 296 B | Rogue faster + fewer B |
| 3b | Object-path at 25 handler types | 72.77 ns / 48 B | n/a | O(1) scaling (1 vs 25 identical) |
| 4 | Publish — 2 event handlers | **113.0 ns** / **112 B** | 195.8 ns / 464 B | Rogue faster + fewer B |
| 5 | Publish — 5 event handlers | **238.9 ns** / **208 B** | 364.4 ns / 920 B | Rogue faster + fewer B |
| 6 | Publish — 20 event handlers | **861.3 ns** / **688 B** | 1,293.4 ns / 3,200 B | Rogue faster + fewer B |
| 7 | CreateStream (10 items) | 361.9 ns / 344 B | n/a | Rogue-only (MediatR v12 core has no streaming) |
| 8 | Validated `Send` (real `FluentValidation` rule, valid payload, steady-state) | **521.4 ns** / **1.13 KB** | 543.8 ns / 1.27 KB | Rogue faster + fewer B |

As ratios of MediatR's figure to Rogue's: cold start ~20.7× faster with 23× less allocated; typed
`Send` 4.7× less allocated (224 B against 48 B); untyped `Send` 1.6× faster; publish to 2 handlers
1.7× faster, to 5 handlers 1.5× faster, and to 20 handlers 1.5× faster with 4.7× less allocated.

Across the currently-measured head-to-head scenarios, Rogue meets or beats MediatR on **both** wall-clock
and allocation, plus the structural ~20.7× cold-start lead. The 2026-07-13 ShortRun measured that lead
as ~18.5× (22.50 µs against 416.68 µs); the allocation figures are the same in both runs.

Row 8 (added in 1.1.0) is a head-to-head with a real `FluentValidation` behavior in the
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
| `ISender.Send` | 350.4 ns | 355.8 ns | 352.4 ns | 872 B |
| concrete path (`SendChainPingRequest`) | 325.6 ns | 318.9 ns | 320.1 ns | 872 B |

The 872 B is honest: the statically-typed behavior chain removes the per-call pipeline-state boxing, but
each chain link's forwarding lambda plus the transient handler/behavior instances still allocate.

**Concurrency.** Concurrent `Send` across N held DI scopes via `Task.WhenAll`
(`[ThreadingDiagnoser]` + `[MemoryDiagnoser]`):

| Concurrency | Mean | Allocated | Lock Contentions |
|------------:|-----:|----------:|-----------------:|
| 1  |    886.2 ns |  2.71 KB | 0 |
| 4  |  3,241.4 ns | 10.30 KB | 0 |
| 8  |  6,372.6 ns | 20.43 KB | 0 |
| 16 | 13,965.8 ns | 40.68 KB | 0 |

Latency and allocation scale linearly, with **zero lock contention** at every level — the generated
dispatcher holds no shared mutable state.

## Jobs and validation

The jobs benchmarks come from a local ShortRun of every class in `bench/SkathIO.Rogue.Benchmarks.Jobs`
on 2026-11-21; their raw BenchmarkDotNet reports (GitHub Markdown, CSV and HTML for each class) are
in `bench/results/2026-11-21-284630b/`. The same run covered the validation benchmarks, which were
rerun as a full run on 2026-11-22 for the 2.0.0 baseline: the validation tables below come from
`bench/results/2026-11-22-696836e/`, and the 2026-11-21 ShortRun of them stays committed for history.
The setup below is that of the 2026-11-21 run; the 2026-11-22 run used the same laptop, SDK and
runtime, with BenchmarkDotNet's default job.

- **Machine:** a laptop, an MSI GE72VR 6RF: Intel Core i7-6700HQ (Skylake, 2.60 GHz, 4 physical /
  8 logical cores), 15 GiB of RAM as the OS reports it, Ubuntu 24.04.5 LTS. The Docker data, and so
  the SQL Server database files, are on its spinning disk (HGST HTS721010A9E630, 7,200 rpm), not on
  an SSD.
- **SDK / runtime:** .NET 10.0.115 SDK, .NET 10.0.15 runtime (X64 RyuJIT x86-64-v3), BenchmarkDotNet
  0.15.8, ShortRun job (3 iterations, 3 warmup, 1 launch). BenchmarkDotNet could not raise its process
  priority (no permission), so other work on the laptop can show in the times.
- **Code:** commit `284630b`, plus the change that commits these results: `Jobs` moved from the
  shared base class onto each benchmark class, so that the SQL Server class runs 1,000 jobs only.
  That change alters nothing the benchmarks measure.
- **Baselines:** Hangfire.InMemory 1.0.0 and Hangfire.SqlServer 1.8.25 (Hangfire.Core LGPL-3.0, bench
  only, in no shipped package). SQL Server 2022 (16.0.4325.4, image
  `mcr.microsoft.com/mssql/server:2022-latest`) in a local Docker container. Both frameworks use the
  same database, each in a schema of its own, and Hangfire uses the SQL Server settings its
  documentation recommends.
- **Reproduce:**

  ```bash
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks.Jobs -- --filter '*InMemoryJobsBenchmarks*' --job short
  ROGUE_BENCH_SQLSERVER='Server=…;Database=…;User Id=…;Password=…;TrustServerCertificate=True' \
    dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks.Jobs -- --filter '*SqlServerJobsBenchmarks*' --job short
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks.Validation -- --filter '*' --job short
  ```

  Give the SQL Server run an empty database. Jobs left over from an interrupted run are picked up
  by the next one and break its job count.

### Jobs: Rogue vs Hangfire

Each operation enqueues N small jobs and waits until every one has run, with the storage and worker
started once per benchmark at their defaults. Hangfire enqueues one job at a time, because it has no
bulk enqueue outside Hangfire Pro. Rogue is measured with one `Enqueue` per job, with one
`EnqueueMany`, and with `EnqueueMany` plus one service scope per claimed batch. Allocation is
process-wide for the whole operation, the worker included.

**In-memory store, against Hangfire.InMemory.** Time and allocation per job:

| Jobs | Hangfire | Rogue, `Enqueue` each | Rogue, `EnqueueMany` | Rogue, `EnqueueMany`, scope per batch |
|-----:|---------:|----------------------:|---------------------:|--------------------------------------:|
| 1,000   | 71.5 µs / 38.0 KB | **5.0 µs** / 2.0 KB | **4.8 µs** / 1.4 KB | **4.4 µs** / **1.1 KB** |
| 10,000  | 48.1 µs / 37.8 KB | **6.7 µs** / 2.0 KB | **6.5 µs** / 1.4 KB | **4.5 µs** / **1.1 KB** |
| 100,000 | 49.4 µs / 37.8 KB | **5.4 µs** / 2.0 KB | **4.8 µs** / 1.4 KB | **4.8 µs** / **1.1 KB** |

At 100,000 jobs Hangfire.InMemory took 4.94 s and allocated 3.69 GB. Rogue took 0.48 to 0.54 s
and allocated 108 to 197 MB: 9 to 10 times faster, with 19 to 34 times less allocated. Per job,
Rogue with `EnqueueMany` and a scope per batch is 16× faster than Hangfire.InMemory at 1,000 jobs
(4.4 µs against 71.5 µs), 10.7× faster at 10,000 and 10.3× faster at 100,000, and allocates 34× less
(1.1 KB against 37.8 KB).

**SQL Server store, against Hangfire.SqlServer, 1,000 jobs.** Mean per operation and allocated:

| Method | Mean | Per job | Allocated | Ratio |
|--------|-----:|--------:|----------:|------:|
| Hangfire, `Enqueue` each | 3.19 min | 191 ms | 134.4 MB | 1.00 |
| Rogue, `Enqueue` each | **1.99 min** | 120 ms | **29.3 MB** | 0.63 |
| Rogue, `EnqueueMany` | **1.72 min** | 103 ms | **18.6 MB** | 0.54 |
| Rogue, `EnqueueMany`, scope per batch | **1.70 min** | 102 ms | **18.6 MB** | 0.53 |

On this laptop both frameworks are bound by the disk: every committed transaction waits for the
spinning disk, so these times say more about how many commits each framework makes per job than
about its own CPU cost. Rogue finishes in a little over half Hangfire's time: with `EnqueueMany` and a scope per batch it is
1.9× faster (1.70 min against 3.19 min) and allocates 7× less (18.6 MB against 134.4 MB). Bulk enqueue helps
less than in memory because, at this size, each job's claim and completion still wait on the disk.
Expect much shorter times on an SSD or a server, and do not read 100 ms per job as Rogue's cost on
real hardware. The error bands are wide (ShortRun, 3 iterations of minutes each). Allocation counts
everything the process allocated during an operation, so a longer operation also counts more
background polling.

The SQL Server class runs 1,000 jobs only. On this disk one Hangfire operation of 1,000 jobs took
about 3 minutes, so 10,000 and 100,000 would have taken hours to days for each benchmark.

**Exceptions on the one-at-a-time path.** When these results were measured, BenchmarkDotNet
counted first-chance exceptions during Rogue's `Enqueue`-each benchmarks: about one per job on SQL
Server and about 0.3 per job in memory (none with `EnqueueMany`). They came from the worker's
wake-up signal: `JobSignal.Pulse()` caught the `SemaphoreFullException` that
`SemaphoreSlim.Release()` throws when a wake-up is already pending. The committed reports have no
exception column: these counts came from separate runs with `--exceptions` on the same machine,
about 0.26 to 0.32 per job for the in-memory store and 956 for 1,000 jobs on SQL Server (one
operation, `--job dry`). `Pulse()` now releases only when no wake-up is pending, so it throws
nothing. A re-run of the in-memory `Enqueue`-each benchmark with `--exceptions` (`--job short`, same
laptop) counted no exceptions at 1,000, 10,000 and 100,000 jobs, at 3.2 to 3.5 µs and 1.6 KB per
job. Those runs are not committed, and the tables above still show the earlier times. The SQL
Server benchmark was not re-run, since one operation takes minutes on this disk.

### Validation

From the full run in `bench/results/2026-11-22-696836e/`.

**Generated validation against FluentValidation, without dispatch** (`InHouseValidationBenchmarks`).
The same order, with a nested customer and three lines, checked by SkathIO.Rogue.Validation's
generated method and by FluentValidation 12's `Validate`:

| Scenario | FluentValidation | SkathIO.Rogue.Validation | Verdict |
|----------|-----------------:|-------------------------:|---------|
| Valid order | 2,518 ns / 4,064 B | **25.2 ns** / **0 B** | about 100 times faster, allocates nothing |
| Order with two broken lines | 6,198 ns / 8,536 B | **317 ns** / **888 B** | about 20 times faster, about a tenth of the allocation |

**Validated `Send`, Rogue against MediatR** (`ValidationBenchmarks`). The same FluentValidation rule
runs as a pipeline behavior on both sides, on a valid payload:

| Method | Mean | Allocated |
|--------|-----:|----------:|
| Rogue, `ValidationBehavior<,>` | **521.4 ns** | **1.13 KB** |
| MediatR, hand-written validation behavior | 543.8 ns | 1.27 KB |

These are row 8 of the table above. The 2026-11-21 ShortRun measured 515.9 ns against 578.5 ns;
the full run narrows Rogue's lead on time to about 4%, with MediatR's run the noisier of the two
(standard deviation 33 ns). The allocation gap is unchanged.
