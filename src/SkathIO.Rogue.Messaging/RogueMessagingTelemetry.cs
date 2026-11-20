using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// The messaging package's OpenTelemetry sources. Subscribe to <see cref="Name"/> as both an activity
/// source and a meter. Tags carry the message name and outcome, and an exception's type name only,
/// never a body or an exception message.
/// </summary>
/// <remarks>
/// Instruments: <c>rogue.messaging.published</c> (counter) and <c>rogue.messaging.consumed</c> (counter,
/// tagged with <c>outcome</c>: succeeded, retried, dead_lettered, duplicate when the inbox found the message
/// handled before, or released when the consumer stopped while the handlers ran or another delivery held
/// the message's id). Each publish is a
/// <c>rogue.message.publish</c> activity, and each delivery handled is a <c>rogue.message.consume</c>
/// activity whose parent is the publisher's trace context from the envelope.
/// </remarks>
public static class RogueMessagingTelemetry
{
    /// <summary>The activity source and meter name.</summary>
    public const string Name = "SkathIO.Rogue.Messaging";

    internal const string PublishActivity = "rogue.message.publish";
    internal const string ConsumeActivity = "rogue.message.consume";
    internal const string NameTag = "rogue.message.name";
    internal const string AttemptTag = "rogue.message.attempt";
    internal const string OutcomeTag = "outcome";
    internal const string ErrorTypeTag = "error.type";

    internal const string Succeeded = "succeeded";
    internal const string Retried = "retried";
    internal const string DeadLettered = "dead_lettered";
    internal const string Released = "released";
    internal const string Duplicate = "duplicate";

    internal static readonly ActivitySource Source = new ActivitySource(Name);

    internal static readonly Meter Meter = new Meter(Name);

    internal static readonly Counter<long> Published = Meter.CreateCounter<long>("rogue.messaging.published", unit: "{message}", description: "Messages sent to the transport.");

    internal static readonly Counter<long> Consumed = Meter.CreateCounter<long>("rogue.messaging.consumed", unit: "{message}", description: "Deliveries settled, by outcome.");
}
