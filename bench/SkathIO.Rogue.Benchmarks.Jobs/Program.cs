// Runs the jobs benchmarks; pass BenchmarkDotNet arguments such as --filter '*InMemoryJobsBenchmarks*'.
BenchmarkDotNet.Running.BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
