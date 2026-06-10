using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Customers;

public readonly record struct CustomerId(string Value);

public sealed record RegisterCustomerCommand(string Name) : IRequest<CustomerId>;

public sealed class RegisterCustomerCommandHandler : IRequestHandler<RegisterCustomerCommand, CustomerId>
{
    public async Task<CustomerId> Handle(RegisterCustomerCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new CustomerId($"CUST-{request.Name}");
    }
}
