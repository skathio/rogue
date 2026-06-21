using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Orders;

public sealed record CancelOrderCommand(string OrderId) : IRequest;

public sealed class CancelOrderCommandHandler : IRequestHandler<CancelOrderCommand>
{
    public async Task Handle(CancelOrderCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}
