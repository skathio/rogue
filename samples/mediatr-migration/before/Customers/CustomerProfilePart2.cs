using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Customers;

public sealed partial class CustomerProfileQueryHandler
{
    public async Task<CustomerProfile> Handle(GetCustomerProfileQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new CustomerProfile(request.Id, $"{request.Id}@example.com");
    }
}
