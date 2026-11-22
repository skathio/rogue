using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>A service provider wired as an application would wire it, on a clock the test moves.</summary>
internal sealed class MessagingHost : IAsyncDisposable
{
    private MessagingHost(ServiceProvider services) => Services = services;

    public ServiceProvider Services { get; }

    public StepClock Clock => Services.GetRequiredService<StepClock>();

    public RecordingTransport Transport => (RecordingTransport)Services.GetRequiredService<IMessageTransport>();

    public MessageProbe Probe => Services.GetRequiredService<MessageProbe>();

    public MessageConsumer Consumer => Services.GetRequiredService<MessageConsumer>();

    public static MessagingHost Create(
        Action<MessagingOptions>? configure = null,
        TransportCapabilities capabilities = TransportCapabilities.NativeDelay | TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend,
        bool realTime = false,
        Action<IServiceCollection>? services = null)
    {
        // On real time the consumer's idle wait and the transport's delays pass on their own; on the
        // step clock only when the test moves it.
        var clock = new StepClock();
        var transport = new RecordingTransport(new InMemoryTransport(realTime ? TimeProvider.System : clock), capabilities);
        var collection = new ServiceCollection();
        collection.AddSingleton(clock);
        collection.AddSingleton<TimeProvider>(realTime ? TimeProvider.System : clock);
        collection.AddSingleton<MessageProbe>();
        collection.AddSingleton<IMessageTransport>(transport);
        services?.Invoke(collection);
        collection.AddRogue();
        collection.AddRogueMessaging(options =>
        {
            options.SerializerContext = TestMessagesJson.Default;
            configure?.Invoke(options);
        });
        return new MessagingHost(collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));
    }

    public static MessageEnvelope Envelope(string name, string json) =>
        new MessageEnvelope { Name = name, Body = Encoding.UTF8.GetBytes(json) };

    /// <summary>Sends the envelopes and runs the consumer once, moving the clock a day after.</summary>
    public async Task DeliverAsync(params MessageEnvelope[] envelopes)
    {
        await Transport.SendAsync(envelopes, CancellationToken.None);
        await Consumer.RunOnceAsync();
        Clock.Advance(TimeSpan.FromDays(1));
    }

    /// <summary>Runs the consumer <paramref name="rounds"/> times, moving the clock a day after each.</summary>
    public async Task RunRoundsAsync(int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Consumer.RunOnceAsync();
            Clock.Advance(TimeSpan.FromDays(1));
        }
    }

    /// <summary>Takes every message the transport holds ready.</summary>
    public async Task<MessageEnvelope[]> DrainAsync() =>
        (await Transport.ReceiveAsync(MessagingOptions.MaxBatchSize, CancellationToken.None)).Select(static m => m.Envelope).ToArray();

    public IReadOnlyList<DeadLetteredMessage> DeadLettered => Transport.Inner.DeadLettered;

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
