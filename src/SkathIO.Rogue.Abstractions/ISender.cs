using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Sends requests through the pipeline and returns responses.</summary>
public interface ISender
{
    /// <summary>Sends a request and returns its response.</summary>
    ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>Sends a request via the object-dispatch path. Requires <c>EnableObjectDispatch</c>.</summary>
    ValueTask<object?> Send(object request, CancellationToken cancellationToken = default);

#if !NETSTANDARD2_0
    /// <summary>Creates a stream from a streaming request.</summary>
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default);
#endif
}
