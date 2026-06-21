using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Shipping;

// Additional Shipping handlers. Reuses the existing `TrackingNumber` struct from
// DispatchShipmentCommand.cs and introduces a minimal `TrackingStatus` struct.

public readonly record struct TrackingStatus(string Code, string Location);

public sealed record TrackShipmentQuery(string TrackingNumber) : IRequest<TrackingStatus>;

public sealed class TrackShipmentQueryHandler : IRequestHandler<TrackShipmentQuery, TrackingStatus>
{
    public async Task<TrackingStatus> Handle(TrackShipmentQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new TrackingStatus("IN_TRANSIT", "Hub");
    }
}

public sealed record CancelShipmentCommand(string TrackingNumber) : IRequest<bool>;

public sealed class CancelShipmentCommandHandler : IRequestHandler<CancelShipmentCommand, bool>
{
    public async Task<bool> Handle(CancelShipmentCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return request.TrackingNumber.Length > 0;
    }
}

public sealed record GetShipmentHistoryQuery(string OrderId) : IRequest<IReadOnlyList<TrackingNumber>>;

public sealed class GetShipmentHistoryQueryHandler
    : IRequestHandler<GetShipmentHistoryQuery, IReadOnlyList<TrackingNumber>>
{
    public async Task<IReadOnlyList<TrackingNumber>> Handle(
        GetShipmentHistoryQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new[] { new TrackingNumber($"TRK-{request.OrderId}") };
    }
}

// Extra handler to round the total toward ~50.
public sealed record EstimateDeliveryQuery(string Zone) : IRequest<int>;

public sealed class EstimateDeliveryQueryHandler : IRequestHandler<EstimateDeliveryQuery, int>
{
    public async Task<int> Handle(EstimateDeliveryQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return request.Zone.Length + 2;
    }
}
