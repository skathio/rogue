# Benchmarks

Methodology, scenario definitions, and the raw per-run data live in
[`bench/RESULTS.md`](../bench/RESULTS.md) and `bench/results/<date>-<sha>/`. This page summarizes the
setup and the headline findings.

**Status of the numbers:** the competitive comparison in `bench/RESULTS.md` is populated from committed
baseline runs (newest: `bench/results/2026-11-22-696836e/`, a full BenchmarkDotNet run of the core and
validation suites for 2.0.0). The headline finding: **Rogue meets or
beats MediatR on every currently-measured head-to-head scenario** — single-handler `Send` (both the
mean and the generated concrete fast path), the untyped object path, notification fan-out at
N = 2 / 5 / 20, and (new in 1.1.0) validated `Send` with a real `FluentValidation` behavior on both
sides — on both wall-clock and allocated bytes, plus a structural **~20.7× cold-start** lead (no
runtime assembly scan; the earlier 2026-07-13 ShortRun measured ~18.5×). The portable `ISender.Send<T>` interface path allocates **48 B** (one
`ValueTask<T>` box by design — see [Allocation profile](#allocation-profile)); the generated concrete
path adds **0 B** of its own.

## Setup

- Harness: BenchmarkDotNet 0.15.8, default job (no `--job short`) for the 2.0.0 baseline. The jobs
  results are a ShortRun (3 iterations, 3 warmup, 1 launch) from 2026-11-21.
- SDK: .NET 10.0.115 SDK, .NET 10.0.15 runtime (X64 RyuJIT x86-64-v3).
- Hardware: a laptop with an Intel Core i7-6700HQ (Skylake, 4 physical / 8 logical cores) on
  Ubuntu 24.04.5 LTS. Docker, and so the SQL Server database of the jobs benchmarks, runs on its
  spinning disk (7,200 rpm), not an SSD. Expect shorter times on a server, above all for SQL Server.
- Competitor: **MediatR 12.5.0** (Apache-2.0). martinothamar/Mediator was dropped from the suite on
  2026-06-17; the README's feature table still cites it as a positioning comparison, but it is
  no longer a benchmarked dependency. MediatR is deliberately held below v13 (which moved to a
  commercial license) — see [governance](governance.md#dependency-policy).
- Source: `bench/SkathIO.Rogue.Benchmarks` (core scenarios) and
  `bench/SkathIO.Rogue.Benchmarks.Validation` (the validation comparison, new in 1.1.0 — kept in a
  separate project so referencing `SkathIO.Rogue.Validation.FluentValidation` doesn't auto-weave
  `ValidationBehavior<,>` into the core suite's requests; see
  [Auto-discovery from referenced assemblies](behaviors.md#auto-discovery-from-referenced-assemblies)).
  Run with:

  ```bash
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*'
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks.Validation -- --filter '*'
  ```

  Add `--job short` for a quicker, noisier sweep. The jobs suite, `bench/SkathIO.Rogue.Benchmarks.Jobs`,
  is run the same way; its SQL Server class needs a connection string, see
  [`bench/RESULTS.md`](../bench/RESULTS.md#jobs-and-validation).

## Scenarios

The suite covers typed `Send` with zero behaviors, cold-start (first dispatch including DI build),
`Send` + pipeline behaviors, the untyped object path (and its scaling at 25 handler types — evidence
that the generated `switch` stays O(1)), notification fan-out at N = 2 / 5 / 20, a 10-item
stream, `Send` through a real `FluentValidation` validation behavior (steady-state, valid payload —
new in 1.1.0), and a Rogue-only concurrent-dispatch sweep (`Task.WhenAll` over N = 1 / 4 / 8 / 16
independent DI scopes, with `[ThreadingDiagnoser]`). Some cells are `n/a`: MediatR v12's core has no
streaming, and the concurrency and pipeline-depth sweeps are Rogue-only scaling checks, not
comparisons.

## Allocation profile

Rogue's typed dispatch is designed for a zero-allocation concrete path. The published benchmark
exercises the **`ISender.Send<T>` interface path**, which boxes one `ValueTask<T>` by design (measured
48 B for a behavior-free `Send`). The 0-byte claim is on the generated concrete `Send_X` method and is
verified by the unit test `PipelineExecutorTests.Execute_NoBehaviors_ZeroAllocations` via
`GC.GetAllocatedBytesForCurrentThread` (asserting a 0-byte per-operation delta over 1000 iterations),
because that method is internal to the generated assembly and not reachable from a consumer benchmark.
The generated concrete fast path (`RogueDispatcher.Send{Request}`) is the fastest Rogue dispatch
(~30 ns) and adds no allocation of its own; any residual bytes there are the consumer's transient
handler resolution, not dispatch overhead. `bench/RESULTS.md` states this plainly — do not read the
published interface-path number as the concrete-path number.

## Validation engines

`InHouseValidationBenchmarks` in `bench/SkathIO.Rogue.Benchmarks.Validation` checks the same order
(a nested customer and three lines) with `SkathIO.Rogue.Validation`'s generated validator and with
FluentValidation 12's `Validate`, outside dispatch, on a valid order and on one with two broken lines.
These are committed results, from the full run in `bench/results/2026-11-22-696836e/`:

| Method | Mean | Allocated |
|--------|-----:|----------:|
| FluentValidation, valid | 2,518 ns | 4,064 B |
| SkathIO.Rogue.Validation, valid | **25.2 ns** | **0 B** |
| FluentValidation, two broken lines | 6,198 ns | 8,536 B |
| SkathIO.Rogue.Validation, two broken lines | **317 ns** | **888 B** |

The generated validator is about 100 times faster on a valid order and allocates nothing; on the
broken order it is about 20 times faster with about a tenth of the allocation. The same run has the
validated `Send` through a FluentValidation behavior: Rogue 521.4 ns / 1.13 KB against MediatR
543.8 ns / 1.27 KB.

```bash
dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks.Validation -- --filter '*InHouseValidation*'
```

## Jobs: Rogue vs Hangfire

`bench/SkathIO.Rogue.Benchmarks.Jobs` enqueues N small jobs and waits until every one has run, against
Hangfire.InMemory 1.0.0 and Hangfire.SqlServer 1.8.25 (Hangfire.Core is LGPL-3.0, bench only, in no
shipped package). Hangfire enqueues one job at a time, since bulk enqueue is a Hangfire Pro
feature; Rogue is measured with `Enqueue` per job, with `EnqueueMany`, and with `EnqueueMany` plus `JobsOptions.ScopePerBatch`.
Allocation is process-wide for the whole operation, the worker included. These are committed results
from a ShortRun in `bench/results/2026-11-21-284630b/`; they were not rerun for the 2.0.0 baseline.

**In-memory store, against Hangfire.InMemory**, time and allocation per job:

| Jobs | Hangfire | Rogue, `Enqueue` each | Rogue, `EnqueueMany` | Rogue, `EnqueueMany`, scope per batch |
|-----:|---------:|----------------------:|---------------------:|--------------------------------------:|
| 1,000   | 71.5 µs / 38.0 KB | **5.0 µs** / 2.0 KB | **4.8 µs** / 1.4 KB | **4.4 µs** / **1.1 KB** |
| 10,000  | 48.1 µs / 37.8 KB | **6.7 µs** / 2.0 KB | **6.5 µs** / 1.4 KB | **4.5 µs** / **1.1 KB** |
| 100,000 | 49.4 µs / 37.8 KB | **5.4 µs** / 2.0 KB | **4.8 µs** / 1.4 KB | **4.8 µs** / **1.1 KB** |

At 100,000 jobs Rogue is 9 to 10 times faster than Hangfire.InMemory and allocates 19 to 34 times less.

**SQL Server store, against Hangfire.SqlServer, 1,000 jobs** (SQL Server 2022 in Docker):

| Method | Mean | Allocated | Ratio |
|--------|-----:|----------:|------:|
| Hangfire, `Enqueue` each | 3.19 min | 134.4 MB | 1.00 |
| Rogue, `Enqueue` each | **1.99 min** | **29.3 MB** | 0.63 |
| Rogue, `EnqueueMany` | **1.72 min** | **18.6 MB** | 0.54 |
| Rogue, `EnqueueMany`, scope per batch | **1.70 min** | **18.6 MB** | 0.53 |

On this laptop both frameworks wait on the spinning disk for every committed transaction, so these
times count commits per job more than CPU cost: Rogue finishes in a little over half Hangfire's time
and allocates a fifth to a seventh as much. Do not read about 100 ms per job as Rogue's cost on an SSD
or a server. [`bench/RESULTS.md`](../bench/RESULTS.md#jobs-rogue-vs-hangfire) has the full setup,
error bands and caveats.

## Honesty: reporting where Rogue is not fastest

We commit to documenting — honestly and in the open — any realistic scenario where Rogue does **not**
win. This was originally the **notification fan-out**: before the fan-out optimization, Rogue was
~7% slower than MediatR at Publish N=5 and ~24% slower at N=20.

**That gap is now closed.** Making `IEventPublisher.Publish` generic over the event type removed the
per-handler wrapper, its closure, and a boxed enumerator, and caching the publisher plus a
telemetry-gated fast path removed the remaining per-call overhead — flipping both scenarios on both axes:

| Scenario | Rogue | MediatR |
|----------|-------|---------|
| Publish N=2 | **129.9 ns / 112 B** | 218.9 ns / 464 B |
| Publish N=5 | **257.1 ns / 208 B** | 404.8 ns / 920 B |
| Publish N=20 | **1,054.5 ns / 688 B** | 1,389.2 ns / 3,200 B |

As of the current baseline there is **no measured head-to-head scenario where MediatR beats Rogue**.
The honesty commitment stands regardless: if a future change (or a higher fan-out N) reintroduces a
loss, it must be reported here and in `bench/RESULTS.md` in the same plain terms. The superseded
"Rogue is slower" figures are retained verbatim in `bench/RESULTS.md` as the historical record and the
"before" baseline the optimization work improved on.

## CI regression gate

The `bench-smoke` CI job parses the BenchmarkDotNet report artifact and reports the measured
interface-path allocation for visibility, gating pass/fail on the committed contract in
`.bench/thresholds.json` (the concrete path's 0-byte promise, plus a dynamic interface-path allocation
ceiling). Wall-clock latency is recorded for visibility only — it is not a hard gate, because
shared-runner timing is unreliable.
