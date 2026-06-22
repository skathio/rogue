# Benchmarks

Methodology, scenario definitions, and the raw per-run data live in
[`bench/RESULTS.md`](../bench/RESULTS.md) and `bench/results/<date>-<sha>/`. This page summarizes the
setup and the headline findings.

**Status of the numbers:** the competitive comparison in `bench/RESULTS.md` is populated from committed
baseline runs (newest: `bench/results/2026-06-20-a734d6f/`, after the warm-path and notification
fan-out optimizations). The headline finding: **Rogue meets or
beats MediatR on every currently-measured head-to-head scenario** — single-handler `Send` (both the
mean and the generated concrete fast path), the untyped object path, and notification fan-out at
N = 2 / 5 / 20 — on both wall-clock and allocated bytes, plus a structural **~19× cold-start** lead (no
runtime assembly scan). The portable `ISender.Send<T>` interface path allocates **48 B** (one
`ValueTask<T>` box by design — see [Allocation profile](#allocation-profile)); the generated concrete
path adds **0 B** of its own.

## Setup

- Harness: BenchmarkDotNet 0.15.2, ShortRun job (3 iterations, 3 warmup, 1 launch).
- SDK: .NET 10 (X64 RyuJIT AVX2).
- Competitor: **MediatR 12.4.1** (Apache-2.0). martinothamar/Mediator was dropped from the suite on
  2026-06-17; the README's feature table still cites it as a positioning comparison, but it is
  no longer a benchmarked dependency.
- Source: `bench/SkathIO.Rogue.Benchmarks`. Run with:

  ```bash
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*' --job short
  ```

## Scenarios

The suite covers typed `Send` with zero behaviors, cold-start (first dispatch including DI build),
`Send` + pipeline behaviors, the untyped object path (and its scaling at 25 handler types — evidence
that the generated `switch` stays O(1)), notification fan-out at N = 2 / 5 / 20, a 10-item
stream, and a Rogue-only concurrent-dispatch sweep (`Task.WhenAll` over N = 1 / 4 / 8 / 16 independent
DI scopes, with `[ThreadingDiagnoser]`). Some cells are `n/a`: MediatR v12's core has no streaming, and
the concurrency and pipeline-depth sweeps are Rogue-only scaling checks, not comparisons.

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

## Honesty: reporting where Rogue is not fastest

We commit to documenting — honestly and in the open — any realistic scenario where Rogue does **not**
win. This was originally the **notification fan-out**: before the fan-out optimization, Rogue was
~7% slower than MediatR at Publish N=5 and ~24% slower at N=20.

**That gap is now closed.** Making `IEventPublisher.Publish` generic over the event type removed the
per-handler wrapper, its closure, and a boxed enumerator, and caching the publisher plus a
telemetry-gated fast path removed the remaining per-call overhead — flipping both scenarios on both axes:

| Scenario | Rogue | MediatR |
|----------|-------|---------|
| Publish N=2 | **120.6 ns / 112 B** | 209.9 ns / 464 B |
| Publish N=5 | **258.0 ns / 208 B** | 409.6 ns / 920 B |
| Publish N=20 | **883.6 ns / 688 B** | 1,459.2 ns / 3,200 B |

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
