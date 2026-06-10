using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SkathIO.Rogue;

/// <summary>
/// Observability shim (FR-45). Emits an <see cref="Activity"/> per dispatch plus a dispatch count
/// and duration histogram, all under the <c>SkathIO.Rogue</c> source/meter name. Built only on
/// <c>System.Diagnostics.DiagnosticSource</c> (NFR-DEP-2 — no third-party deps on core).
/// </summary>
/// <remarks>
/// Telemetry is gated twice: once at registration via <see cref="RogueOptions.EnableTelemetry"/>
/// (<see cref="Enabled"/>) and again at the call site via <see cref="ActivitySource.HasListeners"/>.
/// When disabled or unsubscribed, <see cref="StartDispatch{TRequest}"/> returns <c>null</c> and
/// <see cref="StopDispatch"/> short-circuits, so the path is effectively zero-overhead: no
/// <see cref="Activity"/> is started, no histogram measurement is recorded, and
/// <see cref="Activity.Current"/> is left untouched.
/// </remarks>
public static class RogueTelemetry
{
    /// <summary>The source/meter name for all SkathIO.Rogue instrumentation.</summary>
    public const string Name = "SkathIO.Rogue";

    private static readonly ActivitySource Source = new ActivitySource(Name);
    private static readonly Meter Meter = new Meter(Name);

    private static readonly Counter<long> DispatchCount =
        Meter.CreateCounter<long>("rogue.dispatch.count", unit: "{dispatch}", description: "Number of request dispatches.");

    private static readonly Histogram<double> DispatchDuration =
        Meter.CreateHistogram<double>("rogue.dispatch.duration", unit: "ms", description: "Request dispatch duration in milliseconds.");

    /// <summary>
    /// Master gate set from <see cref="RogueOptions.EnableTelemetry"/> during <c>AddRogue</c>.
    /// Defaults to <c>false</c>; when <c>false</c> the shim is fully inert regardless of listeners.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// Begins a dispatch scope. Returns <c>null</c> when telemetry is disabled or unsubscribed —
    /// callers must null-check and pass whatever they get back to <see cref="StopDispatch"/>.
    /// </summary>
    /// <typeparam name="TRequest">The request type; its <see cref="Type.Name"/> tags the activity.</typeparam>
    public static DispatchScope? StartDispatch<TRequest>()
        where TRequest : notnull
    {
        if (!Enabled)
        {
            return null;
        }

        // Cheap subscriber check: with no ActivityListener and no MeterListener this is the only
        // work done on the dispatch path, and it leaves Activity.Current untouched.
        bool hasActivityListeners = Source.HasListeners();
        if (!hasActivityListeners && !DispatchCount.Enabled && !DispatchDuration.Enabled)
        {
            return null;
        }

        string requestName = typeof(TRequest).Name;
        Activity? activity = hasActivityListeners
            ? Source.StartActivity("rogue.dispatch", ActivityKind.Internal)
            : null;
        activity?.SetTag("request.type", requestName);

        return new DispatchScope(activity, requestName, ValueStopwatch.StartNew());
    }

    /// <summary>
    /// Ends a dispatch scope started by <see cref="StartDispatch{TRequest}"/>, recording the
    /// duration histogram, incrementing the dispatch counter, and stopping the activity. A
    /// <c>null</c> scope (telemetry off / unsubscribed) is a no-op.
    /// </summary>
    /// <param name="scope">The scope returned by <see cref="StartDispatch{TRequest}"/>.</param>
    /// <param name="exception">The exception that terminated the dispatch, if any.</param>
    public static void StopDispatch(DispatchScope? scope, Exception? exception = null)
    {
        if (scope is null)
        {
            return;
        }

        DispatchScope s = scope.Value;
        double elapsedMs = s.Elapsed.ElapsedMilliseconds;
        string outcome = exception is null ? "success" : "exception";

        var requestTag = new KeyValuePair<string, object?>("request.type", s.RequestName);
        var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);

        if (DispatchCount.Enabled)
        {
            DispatchCount.Add(1, requestTag, outcomeTag);
        }

        if (DispatchDuration.Enabled)
        {
            DispatchDuration.Record(elapsedMs, requestTag, outcomeTag);
        }

        if (s.Activity is { } activity)
        {
            activity.SetTag("outcome", outcome);
            if (exception is not null)
            {
                // NFR-SEC-2: tag the exception *type name*, never its Message — validator/handler
                // exception messages can embed request-payload fragments (e.g. FluentValidation's
                // "'Email' must be a valid email. You entered 'attacker@evil'.") and the span-status
                // description is an export surface. Type name is safe to export.
                activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            }

            activity.Dispose();
        }
    }
}

/// <summary>
/// An in-flight dispatch measurement. Opaque to callers; created by
/// <see cref="RogueTelemetry.StartDispatch{TRequest}"/> and consumed by
/// <see cref="RogueTelemetry.StopDispatch"/>.
/// </summary>
public readonly struct DispatchScope
{
    internal DispatchScope(Activity? activity, string requestName, ValueStopwatch elapsed)
    {
        Activity = activity;
        RequestName = requestName;
        Elapsed = elapsed;
    }

    internal Activity? Activity { get; }

    internal string RequestName { get; }

    internal ValueStopwatch Elapsed { get; }
}
