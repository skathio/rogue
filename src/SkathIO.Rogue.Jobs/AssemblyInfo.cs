using System.Runtime.CompilerServices;

// The worker's single-step entry points (one claim round, one lease renewal) are internal; the jobs
// tests drive them directly so retries, backoff and lease renewal are tested without real time.
[assembly: InternalsVisibleTo("SkathIO.Rogue.Jobs.Tests")]
