using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// The jobs package's OpenTelemetry sources. Subscribe to <see cref="Name"/> as both an activity source
/// and a meter. Tags carry the job kind, queue and outcome, and an exception's type name only, never a
/// payload or an exception message.
/// </summary>
/// <remarks>
/// Instruments: <c>rogue.jobs.enqueued</c> (counter), <c>rogue.jobs.executed</c> (counter, tagged with
/// <c>outcome</c>: succeeded, retried, dead_lettered or released) and <c>rogue.jobs.duration</c>
/// (histogram, milliseconds). Each run is a <c>rogue.job.execute</c> activity.
/// </remarks>
public static class RogueJobsTelemetry
{
    /// <summary>The activity source and meter name.</summary>
    public const string Name = "SkathIO.Rogue.Jobs";

    internal const string ExecuteActivity = "rogue.job.execute";
    internal const string KindTag = "rogue.job.kind";
    internal const string QueueTag = "rogue.job.queue";
    internal const string AttemptTag = "rogue.job.attempt";
    internal const string OutcomeTag = "outcome";
    internal const string ErrorTypeTag = "error.type";

    internal const string Succeeded = "succeeded";
    internal const string Retried = "retried";
    internal const string DeadLettered = "dead_lettered";
    internal const string Released = "released";

    internal static readonly ActivitySource Source = new ActivitySource(Name);

    internal static readonly Meter Meter = new Meter(Name);

    internal static readonly Counter<long> Enqueued = Meter.CreateCounter<long>("rogue.jobs.enqueued", unit: "{job}", description: "Jobs enqueued.");

    internal static readonly Counter<long> Executed = Meter.CreateCounter<long>("rogue.jobs.executed", unit: "{job}", description: "Job runs finished, by outcome.");

    internal static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("rogue.jobs.duration", unit: "ms", description: "Time one job run took.");
}
