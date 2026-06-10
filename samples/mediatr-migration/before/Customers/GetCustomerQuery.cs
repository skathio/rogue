using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Customers;

public sealed record Customer(string Id, string Name);

public sealed record GetCustomerQuery(string Id) : IRequest<Customer>;

public sealed class GetCustomerQueryHandler : IRequestHandler<GetCustomerQuery, Customer>
{
    public async Task<Customer> Handle(GetCustomerQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new Customer(request.Id, $"Customer {request.Id}");
    }
}
