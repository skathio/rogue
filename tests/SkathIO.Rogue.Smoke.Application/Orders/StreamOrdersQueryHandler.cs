using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using SkathIO.Rogue.Smoke.Infrastructure;

namespace SkathIO.Rogue.Smoke.Application.Orders;

public sealed class StreamOrdersQueryHandler(IOrderStore store) : IStreamQueryHandler<StreamOrdersQuery, OrderDto>
{
#pragma warning disable CS1998 // async iterator has no awaits — the store read is synchronous.
    public async IAsyncEnumerable<OrderDto> Handle(
        StreamOrdersQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (OrderRecord record in store.GetAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return OrderDto.FromRecord(record);
        }
    }
#pragma warning restore CS1998
}
