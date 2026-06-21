using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// Handles a query of type <typeparamref name="TQuery"/> returning <typeparamref name="TResponse"/>.
/// Primary CQS handler contract — declares its own <see cref="Handle"/> with no shared
/// request-handler base (PD-40 clean break).
/// </summary>
public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    /// <summary>Handles the query.</summary>
    ValueTask<TResponse> Handle(TQuery query, CancellationToken cancellationToken);
}
