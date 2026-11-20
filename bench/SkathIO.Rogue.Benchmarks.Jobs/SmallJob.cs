using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.JobsBenchmarks;

/// <summary>The small job Rogue runs: one integer in, nothing done but counting it.</summary>
[Job]
public sealed record SmallJob(int Value) : ICommand;

/// <summary>Counts the job, so a benchmark knows when all it enqueued have run.</summary>
public sealed class SmallJobHandler : ICommandHandler<SmallJob>
{
    /// <inheritdoc />
    public ValueTask Handle(SmallJob command, CancellationToken cancellationToken)
    {
        JobCounter.Hit();
        return default;
    }
}

/// <summary>The same job for Hangfire: a static method taking the same integer.</summary>
public static class HangfireSmallJob
{
    /// <summary>Counts the job, as <see cref="SmallJobHandler"/> does.</summary>
    public static void Run(int value) => JobCounter.Hit();
}

[JsonSerializable(typeof(SmallJob))]
internal sealed partial class BenchJobsJson : JsonSerializerContext;
