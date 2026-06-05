using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Handles a request of type <typeparamref name="TRequest"/> returning <typeparamref name="TResponse"/>.</summary>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>Handles the request.</summary>
    ValueTask<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Handles a request of type <typeparamref name="TRequest"/> that returns no value.</summary>
public interface IRequestHandler<in TRequest>
    where TRequest : IRequest<Unit>
{
    /// <summary>Handles the request.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Handle(TRequest request, CancellationToken cancellationToken);
#else
    ValueTask Handle(TRequest request, CancellationToken cancellationToken);
#endif
}
