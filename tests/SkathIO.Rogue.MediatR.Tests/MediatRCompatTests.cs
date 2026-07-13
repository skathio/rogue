using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Compatibility;
using Xunit;

namespace SkathIO.Rogue.MediatR.Tests;

/// <summary>
/// Runtime coverage for the MediatR compat shim (<c>SkathIO.Rogue.MediatR</c>). Nothing prior to this
/// exercised its <b>runtime</b> dispatch — <c>AdapterMappingTests</c> (<c>Generator.Tests</c>) only
/// proves the source generator emits valid code at compile-verification level, and its inline adapter
/// surface is a hand-copied stand-in, not the real shipped types. This suite runs the real
/// <c>SkathIO.Rogue.MediatR</c> assembly end to end via real DI: <c>AddRogue</c>/<c>AddMediatR</c> →
/// resolve → dispatch.
/// </summary>
public sealed class MediatRCompatTests
{
    private static ServiceProvider BuildProvider(Action<ServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<ICallTracker, CallTracker>();
        extra?.Invoke(services);
        services.AddRogue();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    // ── 1. Adapter command (unmarked) maps to ICommand<T>, dispatched via ISender.Send(object) ────

    [Fact]
    public async Task AdapterCommand_Unmarked_MapsToCommand_DispatchesAndReturnsResponse()
    {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<global::SkathIO.Rogue.ISender>();

        object? result = await sender.Send(new CreateOrderRequest { Name = "Ada" }, CancellationToken.None);

        Assert.Equal("created:Ada", result);
    }

    // ── 2. [MapAsQuery]-marked adapter command maps to IQuery<T> ───────────────────────────────────

    [Fact]
    public async Task AdapterCommand_MapAsQuery_MapsToQuery_DispatchesAndReturnsResponse()
    {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<global::SkathIO.Rogue.ISender>();

        object? result = await sender.Send(new GetOrderRequest { OrderId = "42" }, CancellationToken.None);

        Assert.Equal("fetched:42", result);
    }

    // ── 3. Void adapter command (no response) ───────────────────────────────────────────────────────

    [Fact]
    public async Task AdapterVoidCommand_DispatchesAndCompletes_HandlerRuns()
    {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<global::SkathIO.Rogue.ISender>();
        var tracker = scope.ServiceProvider.GetRequiredService<ICallTracker>();

        object? result = await sender.Send(new PingRequest(), CancellationToken.None);

        Assert.Equal(global::SkathIO.Rogue.Unit.Value, result);
        Assert.Equal(new[] { "Ping" }, tracker.Calls);
    }

    // ── 4. Notification fans out to 2 handlers ──────────────────────────────────────────────────────

    [Fact]
    public async Task Notification_Publish_FansOutToBothHandlers()
    {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<global::SkathIO.Rogue.IPublisher>();
        var tracker = scope.ServiceProvider.GetRequiredService<ICallTracker>();

        await publisher.Publish(new OrderCreatedNotification { OrderId = "7" }, CancellationToken.None);

        Assert.Equal(2, tracker.Calls.Count);
        Assert.Contains("First:7", tracker.Calls);
        Assert.Contains("Second:7", tracker.Calls);
    }

    // ── 5. Compatibility.ISender / Compatibility.IMediator are forwarding aliases ──────────────────
    //
    // Compatibility.ISender/IMediator/IPublisher add zero new members over their core counterparts
    // (ISender.cs/IMediator.cs/IPublisher.cs's own "forwarding alias" comments) — verified concretely
    // below via reflection, not just by reading the source. But NOTHING in the shim or the generator
    // registers them as distinct DI service types: only the adapter's own
    // Compatibility.IRequestHandler<,>/<> gets a registration (RegistrationEmitter.cs:277-283), and no
    // class in SkathIO.Rogue.MediatR declares `: Compatibility.ISender`/`IMediator`/`IPublisher` —
    // Mediator.cs's sealed Mediator implements only the core interfaces. So Compatibility.ISender is a
    // pure type-shape marker with no concrete implementer; resolving it from DI without an explicit
    // registration throws (Microsoft.Extensions.DependencyInjection resolves nominally registered
    // types, not structurally-conforming ones). Proving the "drop-in" claim therefore means: (a) the
    // interface really does add nothing beyond the core shape, and (b) once a consumer forwards it to
    // the same underlying scope registration (the only way such a marker interface can be wired), it
    // dispatches identically to the core interface — not that DI resolves it for free.

    [Theory]
    [InlineData(typeof(SkathIO.Rogue.Compatibility.ISender), typeof(global::SkathIO.Rogue.ISender))]
    [InlineData(typeof(SkathIO.Rogue.Compatibility.IMediator), typeof(global::SkathIO.Rogue.IMediator))]
    [InlineData(typeof(SkathIO.Rogue.Compatibility.IPublisher), typeof(global::SkathIO.Rogue.IPublisher))]
    public void CompatibilityInterface_ExtendsCoreInterface_AndDeclaresNoNewMembers(Type compatType, Type coreType)
    {
        Assert.Contains(coreType, compatType.GetInterfaces());
        Assert.Empty(compatType.GetMembers(System.Reflection.BindingFlags.DeclaredOnly
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
    }

    [Fact]
    public void CompatibilityISender_WithoutExplicitRegistration_ThrowsNoServiceRegistered()
    {
        // Documents the actual current state precisely: Compatibility.ISender is a type-level
        // forwarding alias only, with no concrete implementer anywhere in the shim. Resolving it from
        // DI without an explicit registration throws — the negative half of the "drop-in" proof,
        // guarding against a future accidental claim that it "just works" without anyone wiring it up.
        //
        // This is EXPECTED, not a gap: the migration analyzer's own code-fix
        // (ReplaceUsingMediatRCodeFix.cs) rewrites `using MediatR;` to `using SkathIO.Rogue;` — the
        // CORE namespace — never `SkathIO.Rogue.Compatibility`, precisely to avoid this. Its comment
        // states the compat namespace is "an opt-in transitional aid for DI-only call sites
        // (AddMediatR/Unit.Value/ReflectionMediator), which migrators add by hand" — i.e. migrated
        // ISender/IMediator injections resolve against the core interfaces, not these adapter-shaped
        // ones. So Compatibility.ISender/IMediator/IPublisher are markers only; this test is a
        // permanent regression guard for that contract, not documentation of a latent bug.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<SkathIO.Rogue.Compatibility.ISender>());
        Assert.Contains("SkathIO.Rogue.Compatibility.ISender", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompatibilityIMediator_ForwardedToSameCoreRegistration_DispatchesIdentically()
    {
        // The positive half: once a consumer forwards Compatibility.IMediator to the same underlying
        // Scoped core IMediator registration (via ForwardingCompatMediator — the natural, minimal way
        // to wire a pure interface-shape marker with no implementer of its own), the wrapper's Inner
        // is the SAME core instance resolved directly, and dispatch through the adapter interface
        // returns the identical result as dispatch through the core interface for the same command.
        await using var provider = BuildProvider(services =>
            services.AddScoped<SkathIO.Rogue.Compatibility.IMediator>(
                sp => new ForwardingCompatMediator(sp.GetRequiredService<global::SkathIO.Rogue.IMediator>())));
        using var scope = provider.CreateScope();

        var coreMediator = scope.ServiceProvider.GetRequiredService<global::SkathIO.Rogue.IMediator>();
        var compatMediator = scope.ServiceProvider.GetRequiredService<SkathIO.Rogue.Compatibility.IMediator>();
        var wrapper = Assert.IsType<ForwardingCompatMediator>(compatMediator);

        Assert.Same(coreMediator, wrapper.Inner);

        var command = new CreateOrderRequest { Name = "Grace" };
        object? viaCompat = await compatMediator.Send((object)command, CancellationToken.None);
        object? viaCore = await coreMediator.Send((object)command, CancellationToken.None);

        Assert.Equal(viaCore, viaCompat);
        Assert.Equal("created:Grace", viaCompat);
    }

    // ── 6. AddMediatR(cfg => ...) accepted as a drop-in for AddRogue() ──────────────────────────────

    [Fact]
    public async Task AddMediatR_WithConfigureLambda_AcceptsItAndDispatchesNormally()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICallTracker, CallTracker>();

        // The config lambda mirrors MediatR's own RegisterServicesFromAssemblyContaining<T> call
        // shape — accepted and ignored (the generator already did discovery at compile time), per
        // MediatRCompatExtensions.cs's own "drop-in replacement" comment.
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<MediatRCompatTests>());

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<global::SkathIO.Rogue.ISender>();

        object? result = await sender.Send(new CreateOrderRequest { Name = "Turing" }, CancellationToken.None);

        Assert.Equal("created:Turing", result);
    }
}
