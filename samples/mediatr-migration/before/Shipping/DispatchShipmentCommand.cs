using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Shipping;

public readonly record struct TrackingNumber(string Value);

public sealed record DispatchShipmentCommand(string OrderId) : IRequest<TrackingNumber>;

public sealed class DispatchShipmentCommandHandler : IRequestHandler<DispatchShipmentCommand, TrackingNumber>
{
    public async Task<TrackingNumber> Handle(DispatchShipmentCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new TrackingNumber($"TRK-{request.OrderId}");
    }
}
