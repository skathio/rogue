using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Smoke.Infrastructure;

namespace SkathIO.Rogue.Smoke.Application.Orders;

public sealed class ReorderCommandHandler(IOrderStore store, IOrderActivityLog log)
    : ICommandHandler<ReorderCommand, int>
{
    public ValueTask<int> Handle(ReorderCommand request, CancellationToken cancellationToken)
    {
        foreach (ReorderLine line in request.Lines)
        {
            store.Add(line.ProductId, line.Quantity);
        }
        log.Record($"ReorderCommandHandler: placed {request.Lines.Count} from {request.SourceOrderId}");
        return new ValueTask<int>(request.Lines.Count);
    }
}
