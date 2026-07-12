using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// Sends commands and queries through the pipeline and returns responses.
/// <para>
/// <b>Resolve this from a scope, not the root <c>IServiceProvider</c>.</b> The default
/// registration is <c>Scoped</c> (one mediator per request/scope). Resolving it from the root
/// provider — e.g. directly in an <c>IHostedService</c>/<c>BackgroundService</c> constructor, or
/// at startup before a scope exists — throws <see cref="System.InvalidOperationException"/>.
/// Create a scope first with <c>IServiceScopeFactory.CreateScope()</c> and resolve from
/// <c>scope.ServiceProvider</c> instead. See the README's "Scoped dispatch" section for the exact
/// exception text and worked examples.
/// </para>
/// </summary>
public interface ISender
{
    /// <summary>Sends a command that produces no return value.</summary>
    ValueTask Send(ICommand command, CancellationToken cancellationToken = default);

    /// <summary>Sends a command and returns its response.</summary>
    ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default);

    /// <summary>Sends a query and returns its response.</summary>
    ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default);

    /// <summary>Sends a command or query via the object-dispatch path. Requires <c>EnableObjectDispatch</c>.</summary>
    ValueTask<object?> Send(object request, CancellationToken cancellationToken = default);

#if !NETSTANDARD2_0
    /// <summary>Creates a stream from a streaming query.</summary>
    IAsyncEnumerable<TItem> CreateStream<TItem>(IStreamQuery<TItem> query, CancellationToken cancellationToken = default);
#endif
}
