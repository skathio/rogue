using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SkathIO.Rogue.Logging;

/// <summary>
/// A pipeline behavior that logs the request type, outcome (success/exception) and elapsed time for
/// every dispatch. Payloads are <b>never</b> logged by default (NFR-SEC-2); opt in per request type
/// with <see cref="LogPayloadAttribute"/> or globally with <see cref="LoggingOptions.LogPayload"/>.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    // Whether TRequest opted into payload logging via [LogPayload]. Computed once per closed
    // generic — the attribute lookup is reflection, but the JIT instantiates this static per
    // <TRequest, TResponse> so it runs at most once per type pair.
    private static readonly bool RequestOptedIntoPayload =
        typeof(TRequest).GetCustomAttribute<LogPayloadAttribute>() is not null;

    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;
    private readonly bool _logPayload;

    /// <summary>Initializes the behavior.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="options">
    /// Optional logging options. When omitted (not registered in DI) payload logging stays off.
    /// </param>
    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger, LoggingOptions? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logPayload = RequestOptedIntoPayload || (options?.LogPayload ?? false);
    }

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        string requestName = typeof(TRequest).Name;
        var stopwatch = ValueStopwatch.StartNew();

        if (_logPayload)
        {
            // Payload logging explicitly opted in — payload may contain sensitive data by the
            // caller's own choice (NFR-SEC-2 escape hatch).
            _logger.LogInformation("Rogue dispatching {RequestName}: {@Request}", requestName, request);
        }
        else
        {
            _logger.LogInformation("Rogue dispatching {RequestName}", requestName);
        }

        try
        {
            TResponse response = await next().ConfigureAwait(false);

            if (_logPayload)
            {
                _logger.LogInformation(
                    "Rogue dispatched {RequestName}: success in {ElapsedMs}ms with {@Response}",
                    requestName, stopwatch.ElapsedMilliseconds, response);
            }
            else
            {
                _logger.LogInformation(
                    "Rogue dispatched {RequestName}: success in {ElapsedMs}ms",
                    requestName, stopwatch.ElapsedMilliseconds);
            }

            return response;
        }
        catch (Exception ex)
        {
            // Log the failure (with the exception, never the payload unless opted in) and rethrow —
            // the behavior observes, it does not swallow.
            _logger.LogError(
                ex, "Rogue dispatched {RequestName}: exception in {ElapsedMs}ms",
                requestName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
