using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Behaviors.Tests;

// A handled request so the dispatch path can run end-to-end. The generator auto-weaves the
// referenced open behaviors (Logging/Validation) onto it, so the dispatch test registers logging.
public sealed class Ping : IRequest<string> { }
public sealed class PingHandler : IRequestHandler<Ping, string>
{
    public ValueTask<string> Handle(Ping request, CancellationToken ct) => new ValueTask<string>("pong");
}

// A request whose handler throws with a payload-bearing message, used to prove the dispatch path
// tags the span status with the exception *type name* — never the message (NFR-SEC-2 / D6 leak fix).
public sealed class Boom : IRequest<string> { }
public sealed class BoomHandler : IRequestHandler<Boom, string>
{
    public const string SecretMessage = "secret-payload-fragment user@evil.example";
    public ValueTask<string> Handle(Boom request, CancellationToken ct) =>
        throw new InvalidOperationException(SecretMessage);
}

// Telemetry uses a process-global ActivitySource/Meter and a process-global Enabled gate. These
// tests mutate that shared static, so they are collected into a single non-parallel collection.
[Collection(nameof(TelemetryTests))]
[CollectionDefinition(nameof(TelemetryTests), DisableParallelization = true)]
public sealed class TelemetryTests : IDisposable
{
    private readonly bool _originalEnabled = RogueTelemetry.Enabled;

    public void Dispose() => RogueTelemetry.Enabled = _originalEnabled;

    [Fact]
    public async Task Telemetry_NoOverhead_WhenDisabled()
    {
        RogueTelemetry.Enabled = false;

        var svc = new ServiceCollection();
        svc.AddLogging(); // LoggingBehavior<Ping,string> is auto-woven; it needs an ILogger.
        svc.AddRogue(o => o.EnableTelemetry = false);
        await using var sp = svc.BuildServiceProvider();
        var sender = sp.GetRequiredService<ISender>();

        await sender.Send(new Ping());

        // No activity started anywhere on the dispatch path when telemetry is off.
        Assert.Null(Activity.Current);
    }

    [Fact]
    public void StartDispatch_ReturnsNull_WhenDisabled()
    {
        RogueTelemetry.Enabled = false;

        var scope = RogueTelemetry.StartDispatch<Ping>();

        Assert.Null(scope);
        Assert.Null(Activity.Current);
    }

    [Fact]
    public void Telemetry_EmitsActivity_WhenEnabledAndListening()
    {
        RogueTelemetry.Enabled = true;

        var started = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RogueTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = started.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var scope = RogueTelemetry.StartDispatch<Ping>();
        RogueTelemetry.StopDispatch(scope);

        var activity = Assert.Single(started);
        Assert.Equal("rogue.dispatch", activity.OperationName);
        Assert.Equal(nameof(Ping), activity.GetTagItem("request.type"));
        Assert.Equal("success", activity.GetTagItem("outcome"));
    }

    [Fact]
    public void Telemetry_TagsExceptionOutcome_WhenStoppedWithException()
    {
        RogueTelemetry.Enabled = true;

        var started = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RogueTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = started.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var scope = RogueTelemetry.StartDispatch<Ping>();
        RogueTelemetry.StopDispatch(scope, new InvalidOperationException("boom"));

        var activity = Assert.Single(started);
        Assert.Equal("exception", activity.GetTagItem("outcome"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    // FR-45 end-to-end: proves the shim is actually INVOKED by the generated dispatcher (not just
    // callable in isolation, as the unit tests above show). A real ActivityListener observes the
    // rogue.dispatch activity the generated Send_Ping method now emits when telemetry is enabled.
    [Fact]
    public async Task Send_EmitsActivity_WhenTelemetryEnabled()
    {
        RogueTelemetry.Enabled = true;

        var started = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RogueTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = started.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var services = new ServiceCollection();
        services.AddLogging(); // LoggingBehavior<Ping,string> is auto-woven; it needs an ILogger.
        services.AddRogue(o => o.EnableTelemetry = true);
        await using var sp = services.BuildServiceProvider();
        var sender = sp.GetRequiredService<ISender>();

        await sender.Send(new Ping(), CancellationToken.None);

        var activity = Assert.Single(started);
        Assert.Equal("rogue.dispatch", activity.OperationName);
        Assert.Equal(nameof(Ping), activity.GetTagItem("request.type"));
        Assert.Equal("success", activity.GetTagItem("outcome"));
    }

    // D6 leak fix, end-to-end: when a handler throws, the generated dispatch path stops the scope
    // with the exception, the outcome tags "exception", AND the span-status description carries the
    // exception TYPE NAME only — never the (payload-bearing) Message (NFR-SEC-2).
    [Fact]
    public async Task Send_EmitsErrorOutcome_DoesNotLeakExceptionMessage()
    {
        RogueTelemetry.Enabled = true;

        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RogueTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRogue(o => o.EnableTelemetry = true);
        await using var sp = services.BuildServiceProvider();
        var sender = sp.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sender.Send(new Boom(), CancellationToken.None));

        var activity = Assert.Single(stopped);
        Assert.Equal("exception", activity.GetTagItem("outcome"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);

        // The fix: status description is the type name, NOT the leaking message.
        Assert.Equal(nameof(InvalidOperationException), activity.StatusDescription);
        Assert.DoesNotContain(BoomHandler.SecretMessage, activity.StatusDescription ?? string.Empty);
    }
}
