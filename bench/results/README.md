# Benchmark results

Benchmark results are committed after each baseline run.
See the CI `bench-smoke` job for the latest numbers. The `jobs-bench-smoke` and
`validation-bench-smoke` jobs gate the jobs and validation benches against the allocation limits
in `.bench/thresholds.json`, which follow the results committed in `2026-11-21-284630b/`.
The 2.0.0 baseline, a full (non-ShortRun) run of the core and validation suites, is in
`2026-11-22-696836e/`. Run `dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*'`
for a full run, or add `--job short` for a quicker local sweep.
