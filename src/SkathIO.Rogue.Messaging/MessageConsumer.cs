using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// Receives messages from the transport and hands each to its handlers through the code the source
/// generator emits for its stable name, each in a service scope of its own, at most
/// <see cref="MessagingOptions.MaxConcurrency"/> at a time. A failed message is delivered again after an
/// exponential delay until <see cref="MessagingOptions.MaxAttempts"/> deliveries have failed, then
/// dead-lettered. A message with an unknown name, a body over <see cref="MessagingOptions.MaxMessageBytes"/>,
/// or a body that is not JSON of its type within <see cref="MessagingOptions.MaxJsonDepth"/> is
/// dead-lettered at once, without running any handler. When an <see cref="IMessageInbox"/> is registered,
/// a message whose id it reports handled is acked without running its handlers. So is an event whose
/// <see cref="MessageEnvelope.Origin"/> is the transport's own <see cref="IMessageTransport.ServiceName"/>:
/// it was published through <see cref="IPublisher"/> here, and its handlers ran then.
/// </summary>
/// <remarks>
/// Logs carry a message's id and name, never its body. <c>AddRogueMessageConsumer</c> registers a hosted
/// service that calls <see cref="RunAsync"/>; without it nothing is consumed, and a host can still drain
/// messages itself through <see cref="RunOnceAsync"/>. On shutdown, running handlers are cancelled and
/// their messages put back with no delay as a deferral, so being stopped never uses up an attempt.
/// </remarks>
public sealed class MessageConsumer
{
    private const string InvalidText = "(invalid)";

    // The name tag of a delivery whose name no message type has: telemetry keeps one series for all of
    // them, so a sender cannot add a series per made-up name.
    private const string UnknownNameTag = "(unknown)";
    private const int MaxReasonLength = 1000;

    // The longest delay Task.Delay accepts.
    private static readonly TimeSpan _longestWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static readonly Action<ILogger, string, string, int, int, TimeSpan, Exception?> _logRetry =
        LoggerMessage.Define<string, string, int, int, TimeSpan>(LogLevel.Warning, new EventId(1, "MessageRetry"),
            "Message {MessageId} ({MessageName}) failed on delivery {Attempt} of {MaxAttempts}; it is delivered again in {Delay}.");

    private static readonly Action<ILogger, string, string, int, string, Exception?> _logDeadLetter =
        LoggerMessage.Define<string, string, int, string>(LogLevel.Error, new EventId(2, "MessageDeadLettered"),
            "Message {MessageId} ({MessageName}) was dead-lettered on delivery {Attempt}: {Reason}");

    private static readonly Action<ILogger, string, Exception?> _logSettleFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(3, "MessageNotSettled"),
            "Message {MessageId} could not be settled with the transport; it is delivered again.");

    private static readonly Action<ILogger, Exception?> _logReceiveFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(4, "MessageReceiveFailed"),
            "Receiving messages failed; the consumer tries again after its poll interval.");

    private static readonly Action<ILogger, string, Exception?> _logInboxNotUpdated =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5, "MessageInboxNotUpdated"),
            "The inbox could not record the outcome of message {MessageId}; a later delivery of it may run its handlers again once its claim runs out.");

    private readonly MessageRegistry _registry;
    private readonly IMessageTransport _transport;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IMessageInbox? _inbox;

    // Deliveries now waiting out a delay here before they are put back, failed or turned away as busy,
    // each holding a place in the transport's window.
    private int _waitingHere;

    // Built only by AddRogueMessaging, which registers every service read here, the transport aside.
    internal MessageConsumer(MessageRegistry registry, IServiceProvider services)
    {
        _registry = registry;
        _transport = services.GetRequiredService<IMessageTransport>();
        _scopes = services.GetRequiredService<IServiceScopeFactory>();
        _time = services.GetRequiredService<TimeProvider>();
        _logger = (ILogger?)services.GetService<ILoggerFactory>()?.CreateLogger("SkathIO.Rogue.Messaging." + nameof(MessageConsumer)) ?? NullLogger.Instance;
        _inbox = services.GetService<IMessageInbox>();
    }

    /// <summary>
    /// Consumes messages until <paramref name="stoppingToken"/> is cancelled, at most
    /// <see cref="MessagingOptions.MaxConcurrency"/> at a time, and returns once the running handlers
    /// have stopped and their messages are settled. It receives again as soon as a handler finishes,
    /// and waits <see cref="MessagingOptions.PollInterval"/> when nothing was ready. A failed message
    /// waiting out its retry delay does not count against the limit.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        MessagingOptions options = _registry.Options;

        // A slot ends when its handlers are done, before any retry delay; a settle task ends once the
        // message is settled with the transport.
        var running = new List<Task>(options.MaxConcurrency);
        var settling = new List<Task>(options.MaxConcurrency);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                running.RemoveAll(static t => t.IsCompleted);
                settling.RemoveAll(static t => t.IsCompleted);
                int free = options.MaxConcurrency - running.Count;
                if (free > 0 && await StartReceivedAsync(running, settling, Math.Min(free, options.BatchSize), stoppingToken).ConfigureAwait(false) > 0)
                {
                    continue;
                }
                Task wait = free == 0 ? Task.WhenAny(running) : Task.Delay(options.PollInterval, _time, stoppingToken);
                await wait.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            // HandleAsync never throws, so this returns once every received message is settled.
            await Task.WhenAll(settling).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Receives one batch of up to <see cref="MessagingOptions.BatchSize"/> ready messages, handles them,
    /// at most <see cref="MessagingOptions.MaxConcurrency"/> at a time, and returns how many it received
    /// once each is settled. For tests and for hosts that drain messages on a schedule.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        MessagingOptions options = _registry.Options;
        IReadOnlyList<ReceivedMessage> received = await _transport.ReceiveAsync(options.BatchSize, cancellationToken).ConfigureAwait(false);

        // The loop itself is not cancellable: every received message must be settled, never abandoned.
        // A message holds its place in the loop only until its handlers are done, not through its retry delay.
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency };
        var settling = new Task[received.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, received.Count), parallel, async (i, _) =>
        {
            TaskCompletionSource slot = NewSlot();
            settling[i] = HandleAsync(received[i], cancellationToken, slot);
            await slot.Task.ConfigureAwait(false);
        }).ConfigureAwait(false);
        await Task.WhenAll(settling).ConfigureAwait(false);
        return received.Count;
    }

    /// <summary>
    /// Handles one delivery and settles it with the transport; returns the outcome's telemetry name.
    /// Never throws. <paramref name="slot"/>, when given, is set once the delivery no longer needs a
    /// concurrency slot: when its handlers are done, before any retry delay waited here.
    /// </summary>
    internal async Task<string> HandleAsync(ReceivedMessage delivery, CancellationToken stoppingToken, TaskCompletionSource? slot = null)
    {
        try
        {
            return await DecideAndSettleAsync(delivery, stoppingToken, slot).ConfigureAwait(false);
        }
        finally
        {
            slot?.TrySetResult();
        }
    }

    private async Task<string> DecideAndSettleAsync(ReceivedMessage delivery, CancellationToken stoppingToken, TaskCompletionSource? slot)
    {
        MessageEnvelope envelope = delivery.Envelope;
        string name = envelope.Name is not null && _registry.TryGet(envelope.Name, out _) ? envelope.Name : UnknownNameTag;
        ActivityContext.TryParse(envelope.TraceParent, null, out ActivityContext parent);
        using Activity? activity = RogueMessagingTelemetry.Source.StartActivity(RogueMessagingTelemetry.ConsumeActivity, ActivityKind.Consumer, parent);
        activity?.SetTag(RogueMessagingTelemetry.NameTag, name);
        activity?.SetTag(RogueMessagingTelemetry.AttemptTag, delivery.DeliveryCount);

        Outcome outcome = await DecideAsync(delivery, stoppingToken).ConfigureAwait(false);
        await SettleAsync(delivery, outcome, stoppingToken, slot).ConfigureAwait(false);

        var tags = new TagList
        {
            { RogueMessagingTelemetry.NameTag, name },
            { RogueMessagingTelemetry.OutcomeTag, outcome.Name },
        };
        if (outcome.Exception is not null)
        {
            string errorType = outcome.Exception.GetType().Name;
            tags.Add(RogueMessagingTelemetry.ErrorTypeTag, errorType);
            activity?.SetTag(RogueMessagingTelemetry.ErrorTypeTag, errorType);
            activity?.SetStatus(ActivityStatusCode.Error);
        }
        activity?.SetTag(RogueMessagingTelemetry.OutcomeTag, outcome.Name);
        RogueMessagingTelemetry.Consumed.Add(1, tags);
        return outcome.Name;
    }

    /// <summary>
    /// The delay before delivery <paramref name="attempt"/> + 1 of a failed message (one-based): the
    /// first delay doubled for each attempt after the first, capped. Never negative.
    /// </summary>
    internal static TimeSpan RetryDelay(TimeSpan first, int attempt, TimeSpan max)
    {
        if (first <= TimeSpan.Zero || max <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        double doubled = first.Ticks * Math.Pow(2, Math.Max(0, attempt - 1));
        return doubled >= max.Ticks ? max : TimeSpan.FromTicks((long)doubled);
    }

    /// <summary>
    /// A name or id from the wire as it may be logged: itself when it is a valid message name, otherwise
    /// a placeholder, so a sender cannot put a body, or anything else of any length, into the logs.
    /// </summary>
    internal static string Loggable(string? value) => MessageLimits.IsValidName(value) ? value! : InvalidText;

    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return true;
        }
        int end = contentType.IndexOf(';', StringComparison.Ordinal);
        ReadOnlySpan<char> mediaType = (end < 0 ? contentType.AsSpan() : contentType.AsSpan(0, end)).Trim();
        return mediaType.Equals(MessageEnvelope.JsonContentType, StringComparison.OrdinalIgnoreCase);
    }

    // Whether the envelope names this service as its origin. The origin is the sender's claim, but a
    // forged one only drops events the forger itself sent.
    private bool IsOwnEvent(MessageEnvelope envelope)
        => _transport.ServiceName is { } own && string.Equals(envelope.Origin, own, StringComparison.Ordinal);

    private static string TrimReason(string reason)
    {
        reason = reason.Replace("\0", string.Empty, StringComparison.Ordinal);
        return reason.Length <= MaxReasonLength ? reason : reason.Substring(0, MaxReasonLength);
    }

    // Another delivery holds the id and may finish soon. With no retry delay set, the poll interval still
    // spaces the tries, so duplicates of one id cannot come round again at the transport's speed.
    private static TimeSpan BusyDelay(MessagingOptions options) => options.RetryDelay > TimeSpan.Zero ? options.RetryDelay : options.PollInterval;

    private static TaskCompletionSource NewSlot() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    // Receives up to maxCount messages and starts handling each; returns how many. A failed receive is
    // logged and counts as none, so the caller waits its poll interval before trying again.
    private async Task<int> StartReceivedAsync(List<Task> running, List<Task> settling, int maxCount, CancellationToken stoppingToken)
    {
        IReadOnlyList<ReceivedMessage> received;
        try
        {
            received = await _transport.ReceiveAsync(maxCount, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logReceiveFailed(_logger, ex);
            return 0;
        }
        foreach (ReceivedMessage delivery in received)
        {
            TaskCompletionSource slot = NewSlot();
            settling.Add(HandleAsync(delivery, stoppingToken, slot));
            running.Add(slot.Task);
        }
        return received.Count;
    }

    // Checks the untrusted envelope before anything reads its body, then runs the handlers.
    private async Task<Outcome> DecideAsync(ReceivedMessage delivery, CancellationToken stoppingToken)
    {
        MessagingOptions options = _registry.Options;
        MessageEnvelope envelope = delivery.Envelope;
        if (envelope.Body.Length > options.MaxMessageBytes)
        {
            return Outcome.DeadLetter("The body is " + envelope.Body.Length + " bytes, over the limit of " + options.MaxMessageBytes + ".");
        }
        if (envelope.Name is null || !_registry.TryGet(envelope.Name, out BoundMessage? bound))
        {
            return Outcome.DeadLetter("No message type has this name.");
        }
        if (!bound.CanDispatch)
        {
            return Outcome.DeadLetter("This process has no handler for the message.");
        }
        if (!IsJson(envelope.ContentType))
        {
            return Outcome.DeadLetter("The content type is not " + MessageEnvelope.JsonContentType + ".");
        }

        object message;
        try
        {
            message = bound.Deserialize(envelope.Body, options.MaxJsonDepth);
        }
        catch (Exception ex)
        {
            // Any failure to read the body, a custom converter's included, is the body's fault and no
            // retry fixes it. The exception is not logged or kept: its message may quote the body.
            return Outcome.DeadLetter("The body is not a '" + bound.Name + "' message in JSON nested at most " + options.MaxJsonDepth + " deep (" + ex.GetType().Name + ").");
        }
        // Only an event runs its local handlers when it is published; a command is handled wherever it
        // comes from, whatever origin it carries.
        if (message is IEvent && IsOwnEvent(envelope))
        {
            return Outcome.Duplicate;
        }

        return _inbox is null
            ? await RunHandlersAsync(delivery, bound, message, stoppingToken).ConfigureAwait(false)
            : await RunThroughInboxAsync(delivery, bound, message, stoppingToken).ConfigureAwait(false);
    }

    private async Task<Outcome> RunHandlersAsync(ReceivedMessage delivery, BoundMessage bound, object message, CancellationToken stoppingToken)
    {
        try
        {
            await DispatchAsync(bound, delivery.Envelope, message, stoppingToken).ConfigureAwait(false);
            return Outcome.Ack;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return Outcome.Release;
        }
        catch (Exception ex)
        {
            return Failed(delivery, ex);
        }
    }

    // Runs the handlers only when the inbox lets this delivery claim the message id, then records the
    // outcome: handled after success, and otherwise the claim dropped so the next delivery can take it.
    private async Task<Outcome> RunThroughInboxAsync(ReceivedMessage delivery, BoundMessage bound, object message, CancellationToken stoppingToken)
    {
        IMessageInbox inbox = _inbox!;
        string name = bound.Name;
        string id = delivery.Envelope.Id;
        if (!MessageLimits.IsValidId(id))
        {
            return Outcome.DeadLetter("The message id is not 1 to " + MessageLimits.MaxIdLength + " characters without white space, so the inbox cannot tell its deliveries apart.");
        }

        InboxClaimResult claim;
        try
        {
            claim = await inbox.TryClaimAsync(name, id, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return Outcome.Release;
        }
        catch (Exception ex)
        {
            return Failed(delivery, ex);
        }
        switch (claim.Outcome)
        {
            case InboxClaim.Handled:
                return Outcome.Duplicate;
            case InboxClaim.Busy:
                return Outcome.Defer(BusyDelay(_registry.Options));
        }

        Outcome outcome = await RunHandlersAsync(delivery, bound, message, stoppingToken).ConfigureAwait(false);
        try
        {
            if (outcome.Name == RogueMessagingTelemetry.Succeeded)
            {
                await inbox.CompleteAsync(name, id, claim.Token, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await inbox.ReleaseAsync(name, id, claim.Token, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // The handlers' outcome stands; only the guard against a later duplicate is weaker.
            _logInboxNotUpdated(_logger, Loggable(id), ex);
        }
        return outcome;
    }

    // A failed delivery is delivered again after its retry delay, or dead-lettered on its last attempt.
    private Outcome Failed(ReceivedMessage delivery, Exception ex)
    {
        MessagingOptions options = _registry.Options;
        if (delivery.DeliveryCount >= options.MaxAttempts)
        {
            return Outcome.DeadLetter("The handler failed on delivery " + delivery.DeliveryCount + " of " + options.MaxAttempts + ".", ex);
        }
        return Outcome.Retry(RetryDelay(options.RetryDelay, delivery.DeliveryCount, options.MaxRetryDelay), ex);
    }

    // An async method of its own, so the inbound context it enters ends when the handlers return.
    private async Task DispatchAsync(BoundMessage bound, MessageEnvelope envelope, object message, CancellationToken stoppingToken)
    {
        AsyncServiceScope scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            InboundContext.Enter(envelope, message);
            await bound.DispatchAsync(scope.ServiceProvider, message, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SettleAsync(ReceivedMessage delivery, Outcome outcome, CancellationToken stoppingToken, TaskCompletionSource? slot)
    {
        MessageEnvelope envelope = delivery.Envelope;
        string id = Loggable(envelope.Id);
        try
        {
            switch (outcome.Name)
            {
                case RogueMessagingTelemetry.Succeeded:
                case RogueMessagingTelemetry.Duplicate:
                    await _transport.AckAsync(delivery, CancellationToken.None).ConfigureAwait(false);
                    break;
                case RogueMessagingTelemetry.Retried:
                    await WaitAndPutBackAsync(delivery, outcome, stoppingToken, slot).ConfigureAwait(false);
                    break;
                case RogueMessagingTelemetry.DeadLettered:
                    _logDeadLetter(_logger, id, Loggable(envelope.Name), delivery.DeliveryCount, outcome.Reason!, outcome.Exception);
                    // The reason travels with the message to wherever dead letters are read, so it names
                    // the exception's type only: its message can quote the body or a secret. The log
                    // above keeps the whole exception.
                    string reason = outcome.Exception is null ? outcome.Reason! : outcome.Reason + " " + outcome.Exception.GetType().FullName;
                    await _transport.DeadLetterAsync(delivery, TrimReason(reason), CancellationToken.None).ConfigureAwait(false);
                    break;
                default:
                    await WaitAndPutBackAsync(delivery, outcome, stoppingToken, slot).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logSettleFailed(_logger, id, ex);
        }
    }

    // Puts a retried or released delivery back after its delay. A failed one is nacked, its count
    // raised; a released one was put back without its handlers failing (turned away as busy, or stopped
    // by shutdown), so it is deferred and keeps every attempt it has left. One that would wait out its
    // delay here holds a place in the transport's unsettled window all the while. Past half the window
    // it is put back at once instead, so neither failing messages nor duplicates of a busy id can stall
    // the rest, and one that always fails still reaches MaxAttempts.
    private async Task WaitAndPutBackAsync(ReceivedMessage delivery, Outcome outcome, CancellationToken stoppingToken, TaskCompletionSource? slot)
    {
        bool held = WaitsHere(outcome.Delay) && TryHoldPlaceToWait();
        TimeSpan delay = held || !WaitsHere(outcome.Delay) ? outcome.Delay : TimeSpan.Zero;
        try
        {
            bool failed = outcome.Name == RogueMessagingTelemetry.Retried;
            if (failed)
            {
                _logRetry(_logger, Loggable(delivery.Envelope.Id), Loggable(delivery.Envelope.Name), delivery.DeliveryCount, _registry.Options.MaxAttempts, delay, outcome.Exception);
            }
            TimeSpan left = await WaitUnlessNativeAsync(delay, stoppingToken, slot).ConfigureAwait(false);
            await (failed ? _transport.NackAsync(delivery, left, CancellationToken.None) : _transport.DeferAsync(delivery, left, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            if (held)
            {
                Interlocked.Decrement(ref _waitingHere);
            }
        }
    }

    // Takes one of the places half the transport's window allows for deliveries waiting here, when one is free.
    private bool TryHoldPlaceToWait()
    {
        int held = Interlocked.Increment(ref _waitingHere);
        if (_transport.UnsettledLimit is int window && held > window / 2)
        {
            Interlocked.Decrement(ref _waitingHere);
            return false;
        }
        return true;
    }

    private bool WaitsHere(TimeSpan delay) => (_transport.Capabilities & TransportCapabilities.NativeDelay) == 0 && delay > TimeSpan.Zero;

    // A transport that cannot hold a nacked message for a delay gets it back at once, so the delay is
    // waited out here. The slot is given up first: otherwise MaxConcurrency failing messages would stop
    // every other message for their whole delay. Shutting down cuts the wait short.
    private async Task<TimeSpan> WaitUnlessNativeAsync(TimeSpan delay, CancellationToken stoppingToken, TaskCompletionSource? slot)
    {
        if (!WaitsHere(delay))
        {
            return delay;
        }
        slot?.TrySetResult();
        try
        {
            // One Task.Delay waits at most _longestWait, so a longer delay is waited in parts.
            for (TimeSpan left = delay; left > TimeSpan.Zero; left -= _longestWait)
            {
                await Task.Delay(left < _longestWait ? left : _longestWait, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the message is put back now.
        }
        return TimeSpan.Zero;
    }

    private readonly struct Outcome
    {
        private Outcome(string name, TimeSpan delay, string? reason, Exception? exception)
        {
            Name = name;
            Delay = delay;
            Reason = reason;
            Exception = exception;
        }

        public static Outcome Ack => new Outcome(RogueMessagingTelemetry.Succeeded, TimeSpan.Zero, null, null);

        /// <summary>
        /// Put back with no delay: the consumer is stopping, so the message is left to the next one. It is
        /// deferred, not nacked, because the handlers did not fail, and a message on its last attempt must
        /// not be dead-lettered for being stopped.
        /// </summary>
        public static Outcome Release => new Outcome(RogueMessagingTelemetry.Released, TimeSpan.Zero, null, null);

        /// <summary>Acked unhandled: the inbox says the message has been handled before, or it is this service's own event.</summary>
        public static Outcome Duplicate => new Outcome(RogueMessagingTelemetry.Duplicate, TimeSpan.Zero, null, null);

        public string Name { get; }

        public TimeSpan Delay { get; }

        /// <summary>Why the message was dead-lettered, in words that never quote the body.</summary>
        public string? Reason { get; }

        /// <summary>The handler's exception, the only exception an outcome keeps.</summary>
        public Exception? Exception { get; }

        /// <summary>Put back unhandled, to come back after <paramref name="delay"/>: another delivery holds its id.</summary>
        public static Outcome Defer(TimeSpan delay) => new Outcome(RogueMessagingTelemetry.Released, delay, null, null);

        public static Outcome Retry(TimeSpan delay, Exception exception) => new Outcome(RogueMessagingTelemetry.Retried, delay, null, exception);

        public static Outcome DeadLetter(string reason, Exception? exception = null) => new Outcome(RogueMessagingTelemetry.DeadLettered, TimeSpan.Zero, reason, exception);
    }
}
