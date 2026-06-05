using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Pipeline behavior that wraps request handling.</summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    /// <summary>Handles the request, optionally calling <paramref name="next"/> to continue the pipeline.</summary>
    ValueTask<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
