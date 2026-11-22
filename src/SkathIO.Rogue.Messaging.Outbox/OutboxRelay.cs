using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.Messaging.Outbox;

/// <summary>
/// The job an outbox stores for one message: the whole envelope, which the relay sends to the
/// transport unchanged, its id included, so a consumer's inbox recognises a second relay of it.
/// </summary>
internal sealed class OutboxRelay
{
    /// <summary>The job kind relays are stored under.</summary>
    public const string Kind = "rogue.messaging.outbox";

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ContentType { get; set; } = MessageEnvelope.JsonContentType;

    public string? CorrelationId { get; set; }

    public string? CausationId { get; set; }

    public string? TraceParent { get; set; }

    public byte[] Body { get; set; } = Array.Empty<byte>();

    public static OutboxRelay From(MessageEnvelope envelope) => new OutboxRelay
    {
        Id = envelope.Id,
        Name = envelope.Name,
        ContentType = envelope.ContentType,
        CorrelationId = envelope.CorrelationId,
        CausationId = envelope.CausationId,
        TraceParent = envelope.TraceParent,
        Body = envelope.Body.ToArray(),
    };

    /// <summary>
    /// The job definition of relays: each sends its envelope through the transport resolved from
    /// <paramref name="services"/>. A failed send is retried with the jobs' backoff, as any job is.
    /// </summary>
    public static JobDefinition Definition(IServiceProvider services) => new JobDefinition<OutboxRelay>(
        Kind,
        new JobDefaults(null, 0, 0, 0),
        OutboxJson.Default.OutboxRelay,
        (_, relay, cancellationToken) => Send(services, relay, cancellationToken));

    public MessageEnvelope ToEnvelope() => new MessageEnvelope
    {
        Id = Id,
        Name = Name,
        ContentType = ContentType,
        CorrelationId = CorrelationId,
        CausationId = CausationId,
        TraceParent = TraceParent,
        Body = Body,
    };

    private static ValueTask Send(IServiceProvider services, OutboxRelay relay, CancellationToken cancellationToken)
        => services.GetRequiredService<IMessageTransport>().SendAsync(new[] { relay.ToEnvelope() }, cancellationToken);
}

/// <summary>The source-generated serializer of the relay job.</summary>
[JsonSerializable(typeof(OutboxRelay))]
internal sealed partial class OutboxJson : JsonSerializerContext
{
}
