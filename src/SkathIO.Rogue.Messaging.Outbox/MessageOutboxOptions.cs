using System;

namespace SkathIO.Rogue.Messaging.Outbox;

/// <summary>Options for the outbox and inbox, set through <c>AddRogueMessagingOutbox</c>.</summary>
public sealed class MessageOutboxOptions
{
    /// <summary>
    /// How long a delivery holds its message's id in the inbox while its handlers run. Another
    /// delivery of the same message is put back until the handlers finish or this runs out; set it
    /// longer than the slowest handler, since one still running when it runs out may run twice.
    /// </summary>
    public TimeSpan InboxLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Checks every option and throws on the first that is out of range.</summary>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InboxLease, TimeSpan.Zero, nameof(InboxLease));
    }
}
