using System;
using System.Text.Json.Serialization;

namespace SkathIO.Rogue.Messaging;

/// <summary>Options for the message bus and the consumer host, set through <c>AddRogueMessaging</c>.</summary>
public sealed class MessagingOptions
{
    /// <summary>The longest batch one receive may take.</summary>
    public const int MaxBatchSize = 10_000;

    // The longest a delivery turned away as busy may stay away. A transport that holds it on the
    // broker, as the RabbitMQ wait queue does, keeps nothing longer than this.
    internal static readonly TimeSpan MaxBusyDelay = TimeSpan.FromHours(24);

    /// <summary>
    /// The source-generated context that serializes message bodies. Required once any
    /// <see cref="MessageAttribute"/> type exists, and each must be covered by a
    /// <c>[JsonSerializable]</c> attribute on it.
    /// </summary>
    public JsonSerializerContext? SerializerContext { get; set; }

    /// <summary>The most messages one consumer handles at the same time.</summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

    /// <summary>How many times a message is handled before it is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>The largest message body sent or handled, in bytes; a larger inbound one is dead-lettered unread.</summary>
    public int MaxMessageBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// The deepest nesting of JSON objects and arrays a body may have, counted as System.Text.Json counts
    /// it (the outermost object is depth 1); a deeper inbound one is dead-lettered.
    /// </summary>
    public int MaxJsonDepth { get; set; } = 32;

    /// <summary>The most deliveries one receive takes from the transport, from 1 to <see cref="MaxBatchSize"/>.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// The delay before the first redelivery of a failed message; it doubles on each later one. Also how
    /// long a delivery the inbox turns away as busy stays away, not doubled; with no retry delay it
    /// stays away for <see cref="PollInterval"/>. At most 24 hours.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest delay before a redelivery.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an idle consumer waits before it receives again, and how long a delivery the inbox
    /// turns away as busy stays away when <see cref="RetryDelay"/> is zero. At most 24 hours when
    /// <see cref="RetryDelay"/> is zero.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>New options, set by <paramref name="configure"/> and checked.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    internal static MessagingOptions Create(Action<MessagingOptions>? configure)
    {
        var options = new MessagingOptions();
        configure?.Invoke(options);
        options.Validate();
        return options;
    }

    /// <summary>Checks every option and throws on the first that is out of range.</summary>
    internal void Validate()
    {
        (int Value, string Name)[] counts =
        {
            (MaxConcurrency, nameof(MaxConcurrency)),
            (MaxAttempts, nameof(MaxAttempts)),
            (MaxMessageBytes, nameof(MaxMessageBytes)),
            (MaxJsonDepth, nameof(MaxJsonDepth)),
            (BatchSize, nameof(BatchSize)),
        };
        foreach ((int value, string name) in counts)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, name);
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BatchSize, MaxBatchSize, nameof(BatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryDelay, TimeSpan.Zero, nameof(RetryDelay));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RetryDelay, MaxBusyDelay, nameof(RetryDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetryDelay, RetryDelay, nameof(MaxRetryDelay));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(PollInterval));
        if (RetryDelay == TimeSpan.Zero)
        {
            // Then the poll interval is how long a busy delivery stays away.
            ArgumentOutOfRangeException.ThrowIfGreaterThan(PollInterval, MaxBusyDelay, nameof(PollInterval));
        }
    }
}
