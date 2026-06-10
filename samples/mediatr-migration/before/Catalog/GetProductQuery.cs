using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Catalog;

public sealed record Product(string Name, decimal Price);

public sealed record GetProductQuery(string Sku) : IRequest<Product>;

public sealed class GetProductQueryHandler : IRequestHandler<GetProductQuery, Product>
{
    public async Task<Product> Handle(GetProductQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new Product($"Product {request.Sku}", 9.99m);
    }
}
