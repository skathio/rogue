using System;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>
/// Request whose handler always throws, used to exercise the FR-26 exception-handler bridge. The
/// registered <see cref="FaultingRequestExceptionHandler"/> marks the exception handled and supplies
/// a fallback response, so a dispatch of this request returns the fallback instead of propagating.
/// </summary>
public sealed record FaultingRequest(string Value) : IRequest<FaultingResponse>;

/// <summary>Response for <see cref="FaultingRequest"/>.</summary>
public sealed record FaultingResponse(string Value);

/// <summary>Handler for <see cref="FaultingRequest"/>; always throws (FR-26).</summary>
public sealed class FaultingRequestHandler : IRequestHandler<FaultingRequest, FaultingResponse>
{
    /// <summary>Message carried by the thrown exception.</summary>
    public const string ThrowMessage = "faulting-request:boom";

    /// <inheritdoc />
    public ValueTask<FaultingResponse> Handle(FaultingRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException(ThrowMessage);
}

/// <summary>
/// FR-26 exception handler for <see cref="FaultingRequest"/>. Marks the exception handled and supplies
/// a fallback response, which the dispatch loop returns instead of re-throwing.
/// </summary>
public sealed class FaultingRequestExceptionHandler
    : IRequestExceptionHandler<FaultingRequest, FaultingResponse, InvalidOperationException>
{
    /// <summary>Value carried by the fallback response.</summary>
    public const string FallbackValue = "faulting-request:fallback";

    /// <inheritdoc />
#if NETSTANDARD2_0
    public ValueTask<Unit> Handle(
        FaultingRequest request,
        InvalidOperationException exception,
        RequestExceptionHandlerState<FaultingResponse> state,
        CancellationToken cancellationToken)
    {
        state.SetHandled(new FaultingResponse(FallbackValue));
        return Unit.Task;
    }
#else
    public ValueTask Handle(
        FaultingRequest request,
        InvalidOperationException exception,
        RequestExceptionHandlerState<FaultingResponse> state,
        CancellationToken cancellationToken)
    {
        state.SetHandled(new FaultingResponse(FallbackValue));
        return default;
    }
#endif
}

/// <summary>
/// FR-26 observe-only exception action for <see cref="FaultingRequest"/>. Records into the scoped
/// tracker; it cannot suppress propagation (the exception handler does that).
/// </summary>
public sealed class FaultingRequestExceptionAction
    : IRequestExceptionAction<FaultingRequest, InvalidOperationException>
{
    /// <summary>Marker recorded when the action observes the exception.</summary>
    public const string Marker = "faulting-request:action";

    private readonly IHandlerCallTracker _tracker;

    /// <summary>Initializes the action with the scoped tracker.</summary>
    public FaultingRequestExceptionAction(IHandlerCallTracker tracker) => _tracker = tracker;

    /// <inheritdoc />
#if NETSTANDARD2_0
    public ValueTask<Unit> Execute(FaultingRequest request, InvalidOperationException exception, CancellationToken cancellationToken)
    {
        _tracker.Record(Marker);
        return Unit.Task;
    }
#else
    public ValueTask Execute(FaultingRequest request, InvalidOperationException exception, CancellationToken cancellationToken)
    {
        _tracker.Record(Marker);
        return default;
    }
#endif
}
