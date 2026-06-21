using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Sends commands and queries through the pipeline and returns responses.</summary>
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
