using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Shipping;

public readonly record struct ShippingRate(string Zone, decimal Cost);

public sealed record GetShippingRateQuery(string Zone) : IRequest<ShippingRate>;

public sealed class GetShippingRateQueryHandler : IRequestHandler<GetShippingRateQuery, ShippingRate>
{
    public async Task<ShippingRate> Handle(GetShippingRateQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new ShippingRate(request.Zone, 5.00m);
    }
}
