using System;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Handles exceptions thrown by the handler.</summary>
public interface IRequestExceptionHandler<in TRequest, TResponse, in TException>
    where TRequest : notnull
    where TException : Exception
{
    /// <summary>Handles the exception. Set <see cref="RequestExceptionHandlerState{TResponse}.Handled"/> to suppress propagation.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Handle(TRequest request, TException exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken);
#else
    ValueTask Handle(TRequest request, TException exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken);
#endif
}

/// <summary>Observe-only exception action that cannot suppress propagation (FR-26 Should).</summary>
public interface IRequestExceptionAction<in TRequest, in TException>
    where TRequest : notnull
    where TException : Exception
{
    /// <summary>Observes the exception without altering propagation.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Execute(TRequest request, TException exception, CancellationToken cancellationToken);
#else
    ValueTask Execute(TRequest request, TException exception, CancellationToken cancellationToken);
#endif
}

/// <summary>State passed to exception handlers; set <see cref="Handled"/> to suppress re-throw.</summary>
public sealed class RequestExceptionHandlerState<TResponse>
{
    /// <summary>Whether the exception has been handled (suppresses propagation).</summary>
    public bool Handled { get; private set; }

    /// <summary>The response to return if the exception is handled.</summary>
    public TResponse? Response { get; private set; }

    /// <summary>Marks the exception as handled with the given response.</summary>
    public void SetHandled(TResponse response)
    {
        Response = response;
        Handled = true;
    }
}
