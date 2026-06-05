using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Post-processor that runs after the handler returns.</summary>
public interface IRequestPostProcessor<in TRequest, in TResponse>
    where TRequest : notnull
{
    /// <summary>Runs after the handler with both the request and its response.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Process(TRequest request, TResponse response, CancellationToken cancellationToken);
#else
    ValueTask Process(TRequest request, TResponse response, CancellationToken cancellationToken);
#endif
}
