using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkathIO.Rogue.Logging;
using Xunit;

namespace SkathIO.Rogue.Behaviors.Tests;

// ── In-memory logger that records every formatted message ────────────────────

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = new List<string>();

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
    }

    public string AllText => string.Join("\n", Messages);
}

// ── Requests with a distinctive payload value to assert on ───────────────────

// Plain records (not ICommand<T>): these exercise LoggingBehavior directly without a dispatch, so
// they need no handler and avoid ROGUE001. Their compiler-generated ToString() renders property
// values, mirroring real DTOs and letting us assert the payload is/isn't in the rendered output.
internal sealed record LoggedRequest
{
    public string Secret { get; init; } = "TOP-SECRET-PAYLOAD-VALUE";
}

[LogPayload]
internal sealed record LoggedRequestWithAttribute
{
    public string Secret { get; init; } = "OPT-IN-PAYLOAD-VALUE";
}

public sealed class LoggingBehaviorTests
{
    private static RequestHandlerDelegate<string> Ok(string value = "ok")
        => () => new ValueTask<string>(value);

    [Fact]
    public async Task LoggingBehavior_LogsRequestNameAndOutcome()
    {
        var logger = new RecordingLogger<LoggingBehavior<LoggedRequest, string>>();
        var behavior = new LoggingBehavior<LoggedRequest, string>(logger);

        var result = await behavior.Handle(new LoggedRequest(), Ok("pong"), CancellationToken.None);

        Assert.Equal("pong", result);
        Assert.Contains(logger.Messages, m => m.Contains(nameof(LoggedRequest)));
        Assert.Contains(logger.Messages, m => m.Contains("success"));
        // No payload leaked by default.
        Assert.DoesNotContain("TOP-SECRET-PAYLOAD-VALUE", logger.AllText);
    }

    [Fact]
    public async Task LoggingBehavior_DoesNotLogPayload_ByDefault()
    {
        var logger = new RecordingLogger<LoggingBehavior<LoggedRequest, string>>();
        var behavior = new LoggingBehavior<LoggedRequest, string>(logger);

        await behavior.Handle(new LoggedRequest(), Ok(), CancellationToken.None);

        // NFR-SEC-2: the payload value must never appear in default log output.
        Assert.DoesNotContain("TOP-SECRET-PAYLOAD-VALUE", logger.AllText);
    }

    [Fact]
    public async Task LoggingBehavior_LogsPayload_WhenAttributePresent()
    {
        var logger = new RecordingLogger<LoggingBehavior<LoggedRequestWithAttribute, string>>();
        var behavior = new LoggingBehavior<LoggedRequestWithAttribute, string>(logger);

        await behavior.Handle(new LoggedRequestWithAttribute(), Ok(), CancellationToken.None);

        // [LogPayload] opts this request type in — the payload value IS logged.
        Assert.Contains("OPT-IN-PAYLOAD-VALUE", logger.AllText);
    }

    [Fact]
    public async Task LoggingBehavior_LogsPayload_WhenOptionsEnabled()
    {
        var logger = new RecordingLogger<LoggingBehavior<LoggedRequest, string>>();
        var behavior = new LoggingBehavior<LoggedRequest, string>(
            logger, new LoggingOptions { LogPayload = true });

        await behavior.Handle(new LoggedRequest(), Ok(), CancellationToken.None);

        // Global opt-in via LoggingOptions.LogPayload.
        Assert.Contains("TOP-SECRET-PAYLOAD-VALUE", logger.AllText);
    }

    [Fact]
    public async Task LoggingBehavior_LogsException_AndRethrows_WithoutPayload()
    {
        var logger = new RecordingLogger<LoggingBehavior<LoggedRequest, string>>();
        var behavior = new LoggingBehavior<LoggedRequest, string>(logger);

        RequestHandlerDelegate<string> boom = () => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.Handle(new LoggedRequest(), boom, CancellationToken.None).AsTask());

        Assert.Contains(logger.Messages, m => m.Contains("exception"));
        Assert.DoesNotContain("TOP-SECRET-PAYLOAD-VALUE", logger.AllText);
    }
}
