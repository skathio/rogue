using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Catalog;

public sealed record ListProductsQuery(int Page) : IRequest<IReadOnlyList<Product>>;

public sealed class ListProductsQueryHandler : IRequestHandler<ListProductsQuery, IReadOnlyList<Product>>
{
    public async Task<IReadOnlyList<Product>> Handle(ListProductsQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new[] { new Product("Sample", 1m) };
    }
}
