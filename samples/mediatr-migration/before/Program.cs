// Before migration: MediatR-based code.
// After running the SkathIO.Rogue migration analyzer code-fix, this becomes Rogue code.
// Run: Add SkathIO.Rogue + SkathIO.Rogue.MediatR; run "Fix all" for ROGM001/ROGM002.
using MediatR;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
var sp = services.BuildServiceProvider();
var sender = sp.GetRequiredService<ISender>();
var result = await sender.Send(new GetProductQuery("ABC"));
System.Console.WriteLine(result.Name);

public record GetProductQuery(string Sku) : IRequest<Product>;
public record Product(string Name, decimal Price);

public class GetProductQueryHandler : IRequestHandler<GetProductQuery, Product>
{
    public async Task<Product> Handle(GetProductQuery request, CancellationToken cancellationToken)
    {
        await Task.Delay(0, cancellationToken);
        return new Product($"Product {request.Sku}", 9.99m);
    }
}
