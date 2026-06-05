#if !NETSTANDARD2_0
using System.Collections.Generic;
using System.Threading;

namespace SkathIO.Rogue;

/// <summary>Pipeline behavior for streaming requests.</summary>
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>Handles the streaming request, optionally calling <paramref name="next"/> to continue.</summary>
    IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

/// <summary>Delegate representing the next step in the streaming pipeline.</summary>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<TResponse>();
#endif
