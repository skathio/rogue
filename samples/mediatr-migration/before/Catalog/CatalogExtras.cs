using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Catalog;

// Additional Catalog handlers (PD-32 fork(a): each domain contributes 5-8 handler classes). All use
// `await Task.CompletedTask; return X;` bodies so the ROGM002 Task->ValueTask signature rewrite leaves
// a compiling async body. Reuses the existing `Product` record from GetProductQuery.cs.

public sealed record DeleteProductCommand(string Sku) : IRequest;

public sealed class DeleteProductCommandHandler : IRequestHandler<DeleteProductCommand>
{
    public async Task Handle(DeleteProductCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}

public sealed record GetProductsByCategoryQuery(string Category) : IRequest<IReadOnlyList<Product>>;

public sealed class GetProductsByCategoryQueryHandler
    : IRequestHandler<GetProductsByCategoryQuery, IReadOnlyList<Product>>
{
    public async Task<IReadOnlyList<Product>> Handle(
        GetProductsByCategoryQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new[] { new Product($"{request.Category} item", 2m) };
    }
}

public sealed record SearchProductsQuery(string Term) : IRequest<IReadOnlyList<Product>>;

public sealed class SearchProductsQueryHandler
    : IRequestHandler<SearchProductsQuery, IReadOnlyList<Product>>
{
    public async Task<IReadOnlyList<Product>> Handle(
        SearchProductsQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new[] { new Product($"Match: {request.Term}", 3m) };
    }
}

// Two extra handlers to round the total toward ~50.
public sealed record GetProductCountQuery(string Category) : IRequest<int>;

public sealed class GetProductCountQueryHandler : IRequestHandler<GetProductCountQuery, int>
{
    public async Task<int> Handle(GetProductCountQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return request.Category.Length;
    }
}

public sealed record ArchiveProductCommand(string Sku) : IRequest<bool>;

public sealed class ArchiveProductCommandHandler : IRequestHandler<ArchiveProductCommand, bool>
{
    public async Task<bool> Handle(ArchiveProductCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return request.Sku.Length > 0;
    }
}
