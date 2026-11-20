using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

public sealed class MessageConsumerTests
{
    private const string Placed = "{\"Id\":1,\"Note\":\"n\"}";

    [Fact]
    public async Task CommandWithAResponse_ReachesItsHandler()
    {
        await using MessagingHost host = MessagingHost.Create();

        await host.DeliverAsync(MessagingHost.Envelope("test.reserve", "{\"Id\":5}"));

        Assert.Equal(new[] { "reserve:5" }, host.Probe.Ran);
        Assert.Empty(host.DeadLettered);
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("APPLICATION/JSON", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("text/plain", false)]
    [InlineData("application/jsonx", false)]
    public async Task ContentType_MustBeJsonOrMissing(string? contentType, bool handled)
    {
        await using MessagingHost host = MessagingHost.Create();
        MessageEnvelope envelope = MessagingHost.Envelope("test.order-placed", Placed);
        envelope.ContentType = contentType!;

        await host.DeliverAsync(envelope);

        Assert.Equal(handled ? 1 : 0, host.Probe.Ran.Count);
        Assert.Equal(handled ? 0 : 1, host.DeadLettered.Count);
    }

    [Theory]
    [InlineData(Placed + " {}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("{\"Id\":\"one\",\"Note\":\"n\"}")]
    public async Task BodyThatIsNotOneMessageInJson_IsDeadLettered_WithoutRunningAHandler(string json)
    {
        await using MessagingHost host = MessagingHost.Create();

        await host.DeliverAsync(MessagingHost.Envelope("test.order-placed", json));

        Assert.Empty(host.Probe.Ran);
        Assert.StartsWith("The body is not a 'test.order-placed' message", Assert.Single(host.DeadLettered).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BodyAConverterRejects_IsDeadLettered_NotLeftUnsettled()
    {
        await using MessagingHost host = MessagingHost.Create();

        await host.DeliverAsync(MessagingHost.Envelope("test.strict", "{\"Value\":1}"));

        Assert.Equal("The body is not a 'test.strict' message in JSON nested at most 32 deep (FormatException).", Assert.Single(host.DeadLettered).Reason);
        Assert.Empty(await host.DrainAsync());
    }

    [Fact]
    public async Task NullName_IsDeadLettered()
    {
        await using MessagingHost host = MessagingHost.Create();
        MessageEnvelope envelope = MessagingHost.Envelope("test.order-placed", Placed);
        envelope.Name = null!;

        await host.DeliverAsync(envelope);

        Assert.Equal("No message type has this name.", Assert.Single(host.DeadLettered).Reason);
    }

    [Fact]
    public async Task RetryDelays_DoubleOnEachDelivery_UpToTheCap()
    {
        await using MessagingHost host = MessagingHost.Create(o =>
        {
            o.MaxAttempts = 4;
            o.RetryDelay = TimeSpan.FromSeconds(5);
            o.MaxRetryDelay = TimeSpan.FromSeconds(12);
        });

        await host.DeliverAsync(MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"fail\"}"));
        await host.RunRoundsAsync(5);

        Assert.Equal(new[] { 5d, 10d, 12d }, host.Transport.NackDelays.Select(static d => d.TotalSeconds));
        Assert.Equal(4, host.Probe.Ran.Count);
        Assert.Equal("The handler failed on delivery 4 of 4. System.InvalidOperationException", Assert.Single(host.DeadLettered).Reason);
    }

    [Fact]
    public async Task TransportWithoutNativeDelay_TheConsumerWaitsTheDelay_ThenNacksWithNone()
    {
        await using MessagingHost host = MessagingHost.Create(o => o.RetryDelay = TimeSpan.FromSeconds(5), TransportCapabilities.None);

        await host.DeliverAsync(MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"fail\"}"));

        Assert.Equal(new[] { TimeSpan.FromSeconds(5) }, host.Clock.Waited);
        Assert.Equal(new[] { TimeSpan.Zero }, host.Transport.NackDelays);
    }

    [Fact]
    public async Task ConsumeActivity_ContinuesThePublishersTrace()
    {
        ActivityTraceId trace = ActivityTraceId.CreateRandom();
        ActivitySpanId span = ActivitySpanId.CreateRandom();
        var seen = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == RogueMessagingTelemetry.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (seen)
                {
                    seen.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using MessagingHost host = MessagingHost.Create();
        MessageEnvelope envelope = MessagingHost.Envelope("test.order-placed", Placed);
        envelope.TraceParent = "00-" + trace + "-" + span + "-01";

        await host.DeliverAsync(envelope);

        Activity consume;
        lock (seen)
        {
            consume = Assert.Single(seen, a => a.TraceId == trace);
        }
        Assert.Equal("rogue.message.consume", consume.OperationName);
        Assert.Equal(span, consume.ParentSpanId);
        Assert.Equal(ActivityKind.Consumer, consume.Kind);
        Assert.Equal("succeeded", consume.GetTagItem("outcome"));
    }

    [Theory]
    [InlineData("orders.created", "orders.created")]
    [InlineData("bad name INJECTED", "(invalid)")]
    [InlineData("{\"Id\":1}", "(invalid)")]
    [InlineData("", "(invalid)")]
    [InlineData(null, "(invalid)")]
    public void NameOrIdFromTheWire_IsLoggedOnlyWhenItIsAValidName(string? value, string logged)
    {
        Assert.Equal(logged, MessageConsumer.Loggable(value));
    }

    [Fact]
    public async Task StoppingWhileAHandlerRuns_NacksTheMessageWithNoDelay_AndItIsDeliveredAgain()
    {
        await using MessagingHost host = MessagingHost.Create();
        await host.Transport.SendAsync(new[] { MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"wait\"}") }, CancellationToken.None);
        ReceivedMessage delivery = Assert.Single(await host.Transport.ReceiveAsync(1, CancellationToken.None));
        using var stopping = new CancellationTokenSource();

        Task<string> handling = host.Consumer.HandleAsync(delivery, stopping.Token);
        await host.Probe.Started.Task;
        await stopping.CancelAsync();

        Assert.Equal("released", await handling);
        Assert.Equal(new[] { TimeSpan.Zero }, host.Transport.NackDelays);
        Assert.Equal(2, Assert.Single(await host.Transport.ReceiveAsync(1, CancellationToken.None)).DeliveryCount);
    }

    [Fact]
    public async Task RunAsync_ConsumesUntilStopped()
    {
        await using MessagingHost host = MessagingHost.Create(o => o.PollInterval = TimeSpan.FromMilliseconds(10), realTime: true);
        host.Probe.RunTarget = 3;
        using var stopping = new CancellationTokenSource();
        Task running = host.Consumer.RunAsync(stopping.Token);

        await host.Transport.SendAsync(Enumerable.Range(1, 3).Select(static i => MessagingHost.Envelope("test.order-placed", "{\"Id\":" + i + ",\"Note\":\"n\"}")).ToArray(), CancellationToken.None);
        await host.Probe.ReachedTarget.Task;
        await stopping.CancelAsync();
        await running;

        Assert.Equal(new[] { "placed:1", "placed:2", "placed:3" }, host.Probe.Ran.OrderBy(static r => r, StringComparer.Ordinal));
        Assert.Empty(host.DeadLettered);
        Assert.Empty(await host.DrainAsync());
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(5, 1, 5)]
    [InlineData(5, 2, 10)]
    [InlineData(5, 3, 12)]
    [InlineData(5, 5000, 12)]
    public void RetryDelay_DoublesFromTheFirst_AndIsCapped(int firstSeconds, int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), MessageConsumer.RetryDelay(TimeSpan.FromSeconds(firstSeconds), attempt, TimeSpan.FromSeconds(12)));
    }
}
