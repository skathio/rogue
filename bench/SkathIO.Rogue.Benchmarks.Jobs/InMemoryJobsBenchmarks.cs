using System;
using BenchmarkDotNet.Attributes;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue.JobsBenchmarks;

/// <summary>
/// Rogue's in-memory store against Hangfire.InMemory: with no database in the way, what is left is each
/// framework's own cost per job, from enqueue to a finished run. CI runs this as a smoke and gates
/// Rogue's allocation per job against <c>jobs_rogue_bytes_per_job_ceiling</c> in .bench/thresholds.json.
/// </summary>
[MemoryDiagnoser]
public class InMemoryJobsBenchmarks : JobsBenchmarks
{
    /// <inheritdoc />
    [Params(1_000, 10_000, 100_000)]
    public override int Jobs { get => base.Jobs; set => base.Jobs = value; }

    /// <inheritdoc />
    protected override JobStorage CreateHangfireStorage() =>
        // Finished jobs expire after a minute rather than Hangfire's day, so the storage does not grow
        // by every job of every iteration.
        new InMemoryStorage(new InMemoryStorageOptions { MaxExpirationTime = TimeSpan.FromMinutes(1) });

    /// <inheritdoc />
    protected override void AddRogueStore(IServiceCollection services)
    {
        // AddRogueJobs registers the in-memory store when no other is registered.
    }
}
