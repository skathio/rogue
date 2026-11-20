using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// The reason a dead-lettered message carries to the transport names the outcome and the exception's
/// type only, never the exception's message, which may quote the body or a secret. The full exception
/// stays in the log.
/// </summary>
public sealed class DeadLetterReasonTests
{
    private const string Failing = "{\"Id\":1,\"Note\":\"fail\"}";
    private const string HandlerMessage = "handler failed";
    private const string InboxMessage = "inbox down: password=hunter2 at db-7";

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task HandlerFailure_ReasonIsTheOutcomeAndTheExceptionTypeOnly(int maxAttempts)
    {
        await using MessagingHost host = MessagingHost.Create(o => o.MaxAttempts = maxAttempts);

        await host.DeliverAsync(MessagingHost.Envelope("test.order-placed", Failing));
        await host.RunRoundsAsync(maxAttempts);

        string reason = Assert.Single(host.DeadLettered).Reason;
        string outcome = "The handler failed on delivery " + maxAttempts + " of " + maxAttempts + ".";
        Assert.StartsWith(outcome, reason, StringComparison.Ordinal);
        AssertOnlyTheTypeName(reason.Substring(outcome.Length), typeof(InvalidOperationException), HandlerMessage);
    }

    [Fact]
    public async Task InboxFailure_ReasonNeverQuotesTheExceptionMessage()
    {
        var inbox = new FailingInbox();
        await using MessagingHost host = MessagingHost.Create(o => o.MaxAttempts = 1, services: s => s.AddSingleton<IMessageInbox>(inbox));

        await host.DeliverAsync(MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"n\"}"));

        string reason = Assert.Single(host.DeadLettered).Reason;
        Assert.DoesNotContain(InboxMessage, reason, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", reason, StringComparison.Ordinal);
        Assert.Contains(nameof(InboxUnavailableException), reason, StringComparison.Ordinal);
        Assert.Empty(host.Probe.Ran);
    }

    [Fact]
    public async Task HandlerFailure_TheLogKeepsTheFullException()
    {
        var logs = new CapturingLoggerFactory();
        await using MessagingHost host = MessagingHost.Create(o => o.MaxAttempts = 1, services: s => s.AddSingleton<ILoggerFactory>(logs));

        await host.DeliverAsync(MessagingHost.Envelope("test.order-placed", Failing));

        Assert.Single(host.DeadLettered);
        Exception? logged = Assert.Single(logs.DeadLetterExceptions());
        Assert.NotNull(logged);
        Assert.IsType<InvalidOperationException>(logged);
        Assert.Equal(HandlerMessage, logged.Message);
    }

    [Fact]
    public async Task InboxFailure_TheLogKeepsTheSameExceptionWithItsMessage()
    {
        var inbox = new FailingInbox();
        var logs = new CapturingLoggerFactory();
        await using MessagingHost host = MessagingHost.Create(o => o.MaxAttempts = 1, services: s =>
        {
            s.AddSingleton<IMessageInbox>(inbox);
            s.AddSingleton<ILoggerFactory>(logs);
        });

        await host.DeliverAsync(MessagingHost.Envelope("test.order-placed", "{\"Id\":1,\"Note\":\"n\"}"));

        Assert.DoesNotContain(InboxMessage, Assert.Single(host.DeadLettered).Reason, StringComparison.Ordinal);
        Exception? logged = Assert.Single(logs.DeadLetterExceptions());
        Assert.NotNull(logged);
        Assert.Same(inbox.Thrown, logged);
        Assert.Equal(InboxMessage, logged.Message);
    }

    // What follows the outcome is the exception's type name, in either its short or full form, and
    // nothing else that is a word: no message text.
    private static void AssertOnlyTheTypeName(string rest, Type type, string message)
    {
        Assert.DoesNotContain(message, rest, StringComparison.Ordinal);
        Assert.Contains(type.Name, rest, StringComparison.Ordinal);
        string withoutType = rest.Replace(type.FullName!, string.Empty, StringComparison.Ordinal).Replace(type.Name, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(withoutType, char.IsLetterOrDigit);
    }

    /// <summary>An inbox whose claim always throws an exception with a message no reason may carry.</summary>
    private sealed class FailingInbox : IMessageInbox
    {
        public InboxUnavailableException Thrown { get; } = new InboxUnavailableException(InboxMessage);

        public ValueTask<InboxClaim> TryClaimAsync(string messageId, CancellationToken cancellationToken) => throw Thrown;

        public ValueTask CompleteAsync(string messageId, CancellationToken cancellationToken) => default;

        public ValueTask ReleaseAsync(string messageId, CancellationToken cancellationToken) => default;
    }

    /// <summary>Records the exception of each dead-letter log entry.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ILogger _logger;

        public CapturingLoggerFactory()
        {
            _logger = DispatchProxy.Create<ILogger, CapturingSink>();
            ((CapturingSink)_logger).Entries = Entries;
        }

        public ConcurrentQueue<(EventId Event, Exception? Exception)> Entries { get; } = new ConcurrentQueue<(EventId, Exception?)>();

        public Exception?[] DeadLetterExceptions() =>
            Entries.Where(static e => e.Event.Name == "MessageDeadLettered").Select(static e => e.Exception).ToArray();

        public ILogger CreateLogger(string categoryName) => _logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>A logger enabled at every level that keeps each entry's event id and exception.</summary>
internal class CapturingSink : DispatchProxy
{
    public ConcurrentQueue<(EventId Event, Exception? Exception)>? Entries { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ILogger.Log))
        {
            Entries!.Enqueue(((EventId)args![1]!, (Exception?)args[3]));
        }
        return targetMethod?.Name == nameof(ILogger.IsEnabled) ? true : null;
    }
}

/// <summary>A failure whose message holds text no dead-letter reason may carry.</summary>
public sealed class InboxUnavailableException : Exception
{
    public InboxUnavailableException(string message)
        : base(message)
    {
    }
}
