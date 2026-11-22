using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>
/// A job whose kind no binding has is measured under one fixed kind, so whoever can write rows into
/// the store cannot add a metric series per made-up kind. The warning log keeps the real kind.
/// </summary>
public sealed class JobUnknownKindTelemetryTests
{
    [Fact]
    public async Task UnknownKinds_AreMeasuredUnderOneFixedKind_AndTheLogNamesTheRealOne()
    {
        // A queue of its own: the listener sees every test's jobs, and only this test uses it.
        string queue = "unknown-kind-" + Guid.NewGuid().ToString("N");
        string[] kinds = { "made.up." + Guid.NewGuid().ToString("N"), "made.up." + Guid.NewGuid().ToString("N") };
        var kindTags = new ConcurrentQueue<(string Instrument, object? Kind)>();
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
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Collect(instrument, tags, queue, kindTags));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Collect(instrument, tags, queue, kindTags));
        listener.Start();
        var logs = new MessageLoggerFactory();
        await using JobsHost host = JobsHost.Create(o => o.Queues.Add(queue), s => s.AddSingleton<ILoggerFactory>(logs));
        NewJob[] jobs = kinds
            .Select(kind => new NewJob { Id = Guid.NewGuid(), Kind = kind, Queue = queue, Payload = Encoding.UTF8.GetBytes("{}"), MaxAttempts = 5 })
            .ToArray();
        await host.Store.EnqueueAsync(jobs, CancellationToken.None);

        await host.Worker.RunOnceAsync();

        (string Instrument, object? Kind)[] measured = kindTags.ToArray();
        Assert.Equal(2, measured.Count(static m => m.Instrument == "rogue.jobs.executed"));
        Assert.Equal(2, measured.Count(static m => m.Instrument == "rogue.jobs.duration"));
        Assert.All(measured, static m => Assert.Equal("(unknown)", m.Kind));
        Assert.All(kinds, kind => Assert.Contains(logs.Messages, message => message.Contains(kind, StringComparison.Ordinal)));
    }

    private static void Collect(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags, string queue, ConcurrentQueue<(string, object?)> into)
    {
        object? kind = null;
        bool ours = false;
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == "rogue.job.kind")
            {
                kind = tag.Value;
            }
            else if (tag.Key == "rogue.job.queue" && (tag.Value as string) == queue)
            {
                ours = true;
            }
        }
        if (ours)
        {
            into.Enqueue((instrument.Name, kind));
        }
    }

    /// <summary>Records the formatted text of each log entry.</summary>
    private sealed class MessageLoggerFactory : ILoggerFactory
    {
        private readonly ILogger _logger;

        public MessageLoggerFactory()
        {
            _logger = DispatchProxy.Create<ILogger, MessageSink>();
            ((MessageSink)_logger).Messages = Messages;
        }

        public ConcurrentQueue<string> Messages { get; } = new ConcurrentQueue<string>();

        public ILogger CreateLogger(string categoryName) => _logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>A logger enabled at every level that keeps each entry's state as text.</summary>
internal class MessageSink : DispatchProxy
{
    public ConcurrentQueue<string>? Messages { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ILogger.Log) && args![2]?.ToString() is string text)
        {
            Messages!.Enqueue(text);
        }
        return targetMethod?.Name == nameof(ILogger.IsEnabled) ? true : null;
    }
}
