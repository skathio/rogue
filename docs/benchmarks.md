# Benchmarks

Methodology, scenario definitions, and (once committed) the raw per-run data live in
[`bench/RESULTS.md`](../bench/RESULTS.md) and `bench/results/<date>-<sha>/`. This page summarizes the
setup and the honesty findings.

**Status of the numbers:** the full competitive comparison (Rogue vs. MediatR vs. martinothamar)
in `bench/RESULTS.md` is currently `TBD` — those cells are populated by the first baseline run
committed to `bench/results/<date>-<sha>/` (see the [release-readiness checklist](governance.md)).
The one figure backed by committed data today is the `ISender.Send<T>` interface-path allocation,
**~384 B** (one `ValueTask<T>` box by design — see [Allocation profile](#allocation-profile) below).
A dry-run allocation sighting is recorded in `bench/RESULTS.md` to show the fan-out *shape* (the A1
honesty finding), but its absolute values and all wall-clock timings are JIT/warmup-dominated and
explicitly **not** authoritative until the committed ShortRun lands.

## Setup

- Harness: BenchmarkDotNet 0.14.0, ShortRun job (5 iterations, 1 warmup).
- SDK: .NET 10.
- Competitors: **MediatR 12.4.1** (Apache-2.0) and **martinothamar/Mediator 3.0.2** (MIT).
- Source: `bench/SkathIO.Rogue.Benchmarks`. Run with:

  ```bash
  dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*' --job short
  ```

## Scenarios

The suite covers typed `Send` with zero behaviors, cold-start (first dispatch including DI build),
`Send` + 3 pipeline behaviors, the untyped object path (and its scaling at 25 handler types, the
PD-3a evidence that the generated `switch` stays O(1)), notification fan-out at N = 2 / 5 / 20, and a
10-item stream. Some cells are intentionally `n/a`: MediatR v12's core has no streaming, and
martinothamar 3.0.2 has no open-generic pipeline behaviors.

## Allocation profile

Rogue's typed dispatch is designed for a zero-allocation concrete path. The published benchmark
exercises the **`ISender.Send<T>` interface path**, which boxes one `ValueTask<T>` by design (measured
~384 B); the 0-byte claim is on the generated concrete `Send_X` method and is verified by the Phase 4
integration tests via `GC.GetAllocatedBytesForCurrentThread`, because that method is internal to the
generated assembly and not reachable from a consumer benchmark. `bench/RESULTS.md` states this
plainly; do not read the published interface-path number as the concrete-path number.

## NFR-PERF-5 honesty: where Rogue is not the fastest

We commit to documenting at least one realistic scenario where Rogue does **not** win
(hypothesis A1). It is the **notification fan-out**:

> Rogue's generated `Publish` resolves handlers at runtime via
> `serviceProvider.GetServices<INotificationHandler<T>>()`, allocating a list (plus a closure per
> handler) on **every** publish. martinothamar bakes a compile-time handler fan-out and performs **no
> per-call DI lookup and no per-call allocation** (~0 B at every N). At N = 20 this difference is
> measurable. This is ordinary domain-event fan-out, not a pathological config.

**A1 status: CONFIRMED on allocation** — Rogue is not the lowest-allocating library on the fan-out
path, and the mechanism is named above. The wall-clock dimension is `TBD` (timing-pending) until the
committed ShortRun run lands in `bench/results/`; the allocation finding stands regardless. This is a
known characteristic with a documented optimization path
(a generator-emitted static handler array per notification type, mirroring martinothamar) — tracked
in the [roadmap](roadmap.md).

## CI regression gate

The `bench-smoke` CI job parses the BenchmarkDotNet report artifact and reports the measured
interface-path allocation for visibility, gating pass/fail on the committed contract in
`.bench/thresholds.json`. Wall-clock latency is recorded for visibility only — it is not a hard gate,
because shared-runner timing is unreliable.
