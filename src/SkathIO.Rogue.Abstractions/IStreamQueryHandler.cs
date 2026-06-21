#if !NETSTANDARD2_0
using System.Collections.Generic;
using System.Threading;

namespace SkathIO.Rogue;

/// <summary>
/// Handles a streaming query of type <typeparamref name="TQuery"/>, yielding a sequence of
/// <typeparamref name="TItem"/>. Primary core stream-handler contract (the streaming analog of
/// <see cref="IQueryHandler{TQuery, TResponse}"/>; promoted/renamed from the former
/// <c>IStreamRequestHandler&lt;,&gt;</c> — PD-40 streaming clean break).
/// </summary>
public interface IStreamQueryHandler<in TQuery, out TItem>
    where TQuery : IStreamQuery<TItem>
{
    /// <summary>Handles the streaming query.</summary>
    IAsyncEnumerable<TItem> Handle(TQuery query, CancellationToken cancellationToken);
}
#endif
