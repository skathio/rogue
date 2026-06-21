using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Catalog;

public sealed record CreateProductCommand(string Name, decimal Price) : IRequest;

public sealed class CreateProductCommandHandler : IRequestHandler<CreateProductCommand>
{
    public async Task Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}
