using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>
/// The package's own activity source and meter. Listeners see every test's jobs, so these tests read
/// only measurements of <see cref="TelemetryJob"/>, which no other test enqueues.
/// </summary>
public sealed class JobTelemetryTests
{
    private const string Kind = "SkathIO.Rogue.Jobs.Tests.TelemetryJob";

    [Fact]
    public async Task EachRun_IsAnActivity_TaggedWithKindAttemptAndOutcome_ButNoMessage()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == RogueJobsTelemetry.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);
        await using JobsHost host = JobsHost.Create();

        await host.Scheduler.EnqueueMany(new[] { new TelemetryJob(false), new TelemetryJob(true) });
        await host.Worker.RunOnceAsync();

        Activity[] runs = stopped.Where(static a => (a.GetTagItem("rogue.job.kind") as string) == Kind).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.All(runs, static a => Assert.Equal("rogue.job.execute", a.OperationName));
        Assert.All(runs, static a => Assert.Equal(1, a.GetTagItem("rogue.job.attempt")));
        Activity failed = Assert.Single(runs, static a => (string?)a.GetTagItem("outcome") == "dead_lettered");
        Assert.Equal("FormatException", failed.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Single(runs, static a => (string?)a.GetTagItem("outcome") == "succeeded");
        Assert.DoesNotContain(runs.SelectMany(static a => a.TagObjects), static t => t.Value is string s && s.Contains("telemetry failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Meter_CountsEnqueuedAndExecutedJobs_ByOutcome_AndTimesEachRun()
    {
        var counters = new ConcurrentQueue<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        var durations = new ConcurrentQueue<Dictionary<string, object?>>();
        using var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, l) =>
            {
                if (instrument.Meter.Name == RogueJobsTelemetry.Name)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            Dictionary<string, object?> map = ToMap(tags);
            if ((map.GetValueOrDefault("rogue.job.kind") as string) == Kind)
            {
                counters.Enqueue((instrument.Name, value, map));
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            Dictionary<string, object?> map = ToMap(tags);
            if (instrument.Name == "rogue.jobs.duration" && (map.GetValueOrDefault("rogue.job.kind") as string) == Kind)
            {
                durations.Enqueue(map);
            }
        });
        listener.Start();
        await using JobsHost host = JobsHost.Create();

        await host.Scheduler.EnqueueMany(new[] { new TelemetryJob(false), new TelemetryJob(true) });
        await host.Worker.RunOnceAsync();

        Assert.Equal(2, counters.Where(static c => c.Instrument == "rogue.jobs.enqueued").Sum(static c => c.Value));
        string?[] outcomes = counters.Where(static c => c.Instrument == "rogue.jobs.executed").Select(static c => c.Tags["outcome"] as string).OrderBy(static o => o, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "dead_lettered", "succeeded" }, outcomes);
        Assert.Equal(2, durations.Count);
        Assert.All(durations, static d => Assert.Equal("default", d["rogue.job.queue"]));
    }

    private static Dictionary<string, object?> ToMap(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            map[tag.Key] = tag.Value;
        }
        return map;
    }
}
