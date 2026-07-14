using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Smoke.Infrastructure;

namespace SkathIO.Rogue.Smoke.Application.Orders;

public sealed class MarkOrderShippedCommandHandler(IOrderStore store, IOrderActivityLog log)
    : ICommandHandler<MarkOrderShippedCommand>
{
    public ValueTask Handle(MarkOrderShippedCommand request, CancellationToken cancellationToken)
    {
        store.MarkShipped(request.OrderId);
        log.Record($"MarkOrderShippedCommandHandler: shipped {request.OrderId}");
        return default;
    }
}
