#if !NETSTANDARD2_0
using System.Collections.Generic;
using System.Threading;

namespace SkathIO.Rogue;

/// <summary>Handles a streaming request of type <typeparamref name="TRequest"/>.</summary>
public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>Handles the streaming request.</summary>
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
#endif
