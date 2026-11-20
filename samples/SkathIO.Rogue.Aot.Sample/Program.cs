// AOT sample — a minimal PublishAot=true host exercising AddRogue() end-to-end.
// Uses a raw ServiceCollection (NOT Host.CreateDefaultBuilder) to keep the dependency
// tree small and trim-clean. The CI aot-publish job publishes this with PublishAot=true
// and fails on any trim/AOT warning (AC-E / NFR-SEC-1 gate).
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using SkathIO.Rogue.Validation;

var services = new ServiceCollection();
services.AddRogue();
using var provider = services.BuildServiceProvider();

var sender = provider.GetRequiredService<ISender>();
var result = await sender.Send(new PingRequest("hello"));
System.Console.WriteLine(result);

// SkathIO.Rogue.Validation under native AOT: a valid request reaches its handler, an invalid one is
// rejected with every failing path, including a collection item's and the async rule's.
int lines = await sender.Send(new PlaceOrder { Reference = "R-1", Lines = new[] { new OrderLine { Sku = "A" } } });
string badLine = await RejectedPaths(sender, new PlaceOrder { Reference = "R-2", Lines = new[] { new OrderLine { Sku = "" } } });
string blocked = await RejectedPaths(sender, new PlaceOrder { Reference = "blocked", Lines = new[] { new OrderLine { Sku = "A" } } });
System.Console.WriteLine(lines + " " + badLine + " " + blocked);

return result == "hello" && lines == 1 && badLine == "Lines[0].Sku" && blocked == "Reference" ? 0 : 1;

static async Task<string> RejectedPaths(ISender sender, PlaceOrder order)
{
    try
    {
        await sender.Send(order);
        return "accepted";
    }
    catch (RogueValidationException ex)
    {
        return string.Join(",", ex.ToProblemDictionary().Keys);
    }
}

namespace SkathIO.Rogue
{
    /// <summary>Sample request returning the echoed payload.</summary>
    public sealed record PingRequest(string Payload) : ICommand<string>;

    /// <summary>Echoes the request payload back to the caller.</summary>
    public sealed class PingHandler : ICommandHandler<PingRequest, string>
    {
        public ValueTask<string> Handle(PingRequest request, CancellationToken cancellationToken)
            => new(request.Payload);
    }

    /// <summary>A validated request: rule attributes, a nested collection and an async rule.</summary>
    [Validate]
    public sealed partial record PlaceOrder : ICommand<int>
    {
        [NotEmpty, MaximumLength(20)] public string Reference { get; init; } = string.Empty;
        [NotEmpty] public IReadOnlyList<OrderLine> Lines { get; init; } = System.Array.Empty<OrderLine>();
    }

    /// <summary>One line of a <see cref="PlaceOrder"/>.</summary>
    [Validate]
    public sealed partial record OrderLine
    {
        [NotEmpty] public string Sku { get; init; } = string.Empty;
    }

    /// <summary>Counts the lines of a valid order.</summary>
    public sealed class PlaceOrderHandler : ICommandHandler<PlaceOrder, int>
    {
        public ValueTask<int> Handle(PlaceOrder request, CancellationToken cancellationToken)
            => new(request.Lines.Count);
    }

    /// <summary>An async rule, registered by the generator and resolved from DI.</summary>
    public sealed class PlaceOrderChecks : IAsyncRules<PlaceOrder>
    {
        public async ValueTask CheckAsync(PlaceOrder request, ValidationErrors errors, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (request.Reference == "blocked")
            {
                errors.Add(nameof(PlaceOrder.Reference), "order.blocked", "This reference is blocked.");
            }
        }
    }
}
