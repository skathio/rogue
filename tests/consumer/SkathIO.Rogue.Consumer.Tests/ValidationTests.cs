using System.Linq;
using System.Threading.Tasks;
using SkathIO.Rogue.Consumer.App;
using SkathIO.Rogue.Validation;
using Xunit;

namespace SkathIO.Rogue.Consumer.Tests;

/// <summary>
/// SkathIO.Rogue.Validation from its package: the app references only that package, so these checks
/// run only if the core generator reached the app through the package's dependency.
/// </summary>
public sealed class ValidationTests
{
    [Fact]
    public async Task AValidRequest_ReachesItsHandler()
    {
        await using var host = new ConsumerHost();

        int number = await host.Mediator.Send(new PlaceOrder { Customer = "Ada", Quantity = 1 });

        Assert.Equal(1, number);
        Assert.Equal(1, host.Orders.Count);
    }

    [Fact]
    public async Task AnInvalidRequest_IsRejected_WithEveryError_AndNeverReachesItsHandler()
    {
        await using var host = new ConsumerHost();

        RogueValidationException ex = await Assert.ThrowsAsync<RogueValidationException>(
            () => host.Mediator.Send(new PlaceOrder { Customer = "", Quantity = 0 }).AsTask());

        Assert.Equal(
            new[] { "Customer:not_empty", "Quantity:greater_than" },
            ex.Errors.Select(static e => e.Path + ":" + e.Code).OrderBy(static s => s, System.StringComparer.Ordinal));
        Assert.Equal(0, host.Orders.Count);
        Assert.DoesNotContain(host.Log.Entries(), static e => e.StartsWith("handled", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACustomerAtTheMaximumLength_IsAccepted()
    {
        await using var host = new ConsumerHost();

        int number = await host.Mediator.Send(new PlaceOrder { Customer = new string('a', 20), Quantity = 1 });

        Assert.Equal(1, number);
    }

    [Fact]
    public async Task ACustomerJustPastTheMaximumLength_IsRejected()
    {
        await using var host = new ConsumerHost();

        RogueValidationException ex = await Assert.ThrowsAsync<RogueValidationException>(
            () => host.Mediator.Send(new PlaceOrder { Customer = new string('a', 21), Quantity = 1 }).AsTask());

        Assert.Equal("Customer:maximum_length", Assert.Single(ex.Errors.Select(static e => e.Path + ":" + e.Code)));
        Assert.Equal(0, host.Orders.Count);
    }
}
