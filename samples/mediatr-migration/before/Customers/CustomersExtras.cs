using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Orders;

namespace Customers;

// Additional Customers handlers. GetCustomerOrdersQuery returns Orders.Order (cross-namespace reuse).

public sealed record UpdateCustomerCommand(string Id, string Name) : IRequest<bool>;

public sealed class UpdateCustomerCommandHandler : IRequestHandler<UpdateCustomerCommand, bool>
{
    public async Task<bool> Handle(UpdateCustomerCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return request.Name.Length > 0;
    }
}

public sealed record DeleteCustomerCommand(string Id) : IRequest;

public sealed class DeleteCustomerCommandHandler : IRequestHandler<DeleteCustomerCommand>
{
    public async Task Handle(DeleteCustomerCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}

public sealed record GetCustomerOrdersQuery(string CustomerId) : IRequest<IReadOnlyList<Order>>;

public sealed class GetCustomerOrdersQueryHandler
    : IRequestHandler<GetCustomerOrdersQuery, IReadOnlyList<Order>>
{
    public async Task<IReadOnlyList<Order>> Handle(
        GetCustomerOrdersQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new[] { new Order($"ORD-{request.CustomerId}", "Open") };
    }
}

public sealed record DeactivateCustomerCommand(string Id) : IRequest<Unit>;

public sealed class DeactivateCustomerCommandHandler
    : IRequestHandler<DeactivateCustomerCommand, Unit>
{
    public async Task<Unit> Handle(DeactivateCustomerCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return Unit.Value;
    }
}
