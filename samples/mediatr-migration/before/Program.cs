// Before migration: MediatR-style handlers invoked directly (no DI container) so the AC-F migration
// gate can compile, fix, recompile, and run this sample end-to-end without a service provider.
// After the SkathIO.Rogue migration code-fixes (ROGM001 using rewrite + ROGM002 Task->ValueTask),
// every handler below becomes Rogue-shaped and this entry point still runs unchanged.
using System.Threading;
using System.Threading.Tasks;
using MediatR;

var cts = new CancellationTokenSource();
var ct = cts.Token;

// Catalog
var product = await new Catalog.GetProductQueryHandler().Handle(new Catalog.GetProductQuery("SKU-1"), ct);
if (product is null) throw new System.InvalidOperationException("Expected a product.");
await new Catalog.CreateProductCommandHandler().Handle(new Catalog.CreateProductCommand("Widget", 1m), ct);

// Orders (GetOrderQuery is already ValueTask-shaped)
var order = await new Orders.GetOrderQueryHandler().Handle(new Orders.GetOrderQuery("ORD-1"), ct);
if (order.Status != "Open") throw new System.InvalidOperationException("Expected an open order.");
var placed = await new Orders.PlaceOrderCommandHandler()
    .Handle(new Orders.PlaceOrderCommand("CUST-1", "SKU-1", 2), ct);
if (!placed.Accepted) throw new System.InvalidOperationException("Expected the order to be accepted.");

// Customers (partial-class handler split across two files)
var profile = await new Customers.CustomerProfileQueryHandler()
    .Handle(new Customers.GetCustomerProfileQuery("CUST-1"), ct);
if (profile.Email.Length == 0) throw new System.InvalidOperationException("Expected a profile email.");

// Inventory
var stock = await new Inventory.CheckStockQueryHandler().Handle(new Inventory.CheckStockQuery("SKU-1"), ct);
if (stock.Available < 0) throw new System.InvalidOperationException("Expected non-negative stock.");

// Notifications (fan-out: two handlers for OrderPlaced)
await new Notifications.OrderPlacedEmailHandler().Handle(new Notifications.OrderPlaced("ORD-1"), ct);
await new Notifications.OrderPlacedAuditHandler().Handle(new Notifications.OrderPlaced("ORD-1"), ct);

System.Console.WriteLine("All sample invocations passed.");
