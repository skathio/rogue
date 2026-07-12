using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.DiResolution.Tests;

/// <summary>
/// Container-boundary regression gate (D2) for the FluentValidation DI-resolution defect (D1/D3).
/// Every case here builds its provider with the exact ASP.NET Core Development-mode strictness
/// (<c>ValidateScopes</c>/<c>ValidateOnBuild</c>) that every pre-existing suite structurally misses
/// (<c>decisions.md#d2</c> Context) — direct behavior construction (Behaviors.Tests), zero validators
/// (WebApi sample), or an unvalidated provider (Generator.Tests' real-DI harness,
/// Integration.Tests' <c>PerfGateTests</c>).
/// </summary>
public sealed class DiResolutionMatrixTests
{
    /// <summary>
    /// Row A analog (design.md §2): default Transient behavior lifetime, Scoped validator,
    /// in-scope <c>Send</c>. Passes today and after the D3 fix — the default configuration is not
    /// broken (design.md §2's headline finding).
    /// </summary>
    [Fact]
    public async Task RowA_DefaultLifetime_ScopedValidator_InScopeSend_Succeeds()
    {
        var services = new ServiceCollection();
        services.AddScoped<IValidator<CreateUserCommand>, CreateUserValidator>();
        services.AddRogue(); // RogueOptions.Lifetime defaults to Transient.

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var result = await sender.Send(new CreateUserCommand { Name = "Ada", Age = 42 }, CancellationToken.None);

        Assert.Equal("created:Ada", result);
    }

    /// <summary>
    /// Row D analog (design.md §2) — the reproduction. <c>RogueOptions.Lifetime = Singleton</c>
    /// (set for handler performance) plus a Scoped <see cref="IValidator{T}"/> is the classic
    /// captive-dependency trap: pre-D3, the generator self-registers the closed
    /// <c>ValidationBehavior&lt;CreateUserCommand,string&gt;</c> (and its
    /// <c>IReadOnlyList&lt;IPipelineBehavior&lt;,&gt;&gt;</c> factory) at <c>options.Lifetime</c>, so a
    /// Singleton behavior ends up consuming a Scoped validator — <c>ValidateOnBuild</c> throws.
    /// <br/><br/>
    /// This test asserts the <b>desired end state</b> (post-D3: behaviors are pinned Transient,
    /// decoupled from <c>options.Lifetime</c>, so this must NOT throw). It is intentionally run
    /// against the pre-2.2 generator as part of this iteration and observed to FAIL — that failure
    /// IS the reproduction deliverable (D2). Iteration 2.2's generator fix is what turns this green.
    /// </summary>
    [Fact]
    public async Task RowD_SingletonLifetime_ScopedValidator_InScopeSend_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddScoped<IValidator<CreateUserCommand>, CreateUserValidator>();
        services.AddRogue(o => o.Lifetime = ServiceLifetime.Singleton);

        // Anti-vacuous-pass (spec.md §11's named risk): explicitly assert the validator's
        // *registered* lifetime is Scoped rather than trusting the AddScoped call above alone — a
        // setup mistake (e.g. an accidental double-registration) must not silently turn this into a
        // no-op pass.
        var validatorDescriptor = services.Single(d => d.ServiceType == typeof(IValidator<CreateUserCommand>));
        Assert.Equal(ServiceLifetime.Scoped, validatorDescriptor.Lifetime);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var result = await sender.Send(new CreateUserCommand { Name = "Ada", Age = 42 }, CancellationToken.None);

        Assert.Equal("created:Ada", result);
    }

    /// <summary>
    /// Root-provider-dispatch case, FluentValidation-referenced half (D1's second failure mode,
    /// design.md §2 rows I/J). Resolving <see cref="ISender"/> from the root
    /// <see cref="IServiceProvider"/> (no <c>CreateScope()</c>) must throw — <c>ISender</c> is
    /// <c>TryAddScoped</c> (<c>RogueServiceCollectionExtensions.cs:34</c>), so under
    /// <c>ValidateScopes</c> the container refuses to hand out a Scoped service from the root. This
    /// is working-as-designed (D2, rogue-perf) and NOT FluentValidation-specific — see
    /// <c>RootScopeDispatchTests</c> in <c>SkathIO.Rogue.Integration.Tests</c> for the bare-Rogue
    /// twin that proves the same throw with no FluentValidation in play at all.
    /// <br/><br/>
    /// The throw fires at <c>GetRequiredService&lt;ISender&gt;()</c> resolution itself, before any
    /// <c>Send</c> call — the assertion wraps the resolution call accordingly.
    /// </summary>
    [Fact]
    public void RootProviderDispatch_FluentValidationReferenced_ThrowsResolvingScopedSender()
    {
        var services = new ServiceCollection();
        services.AddScoped<IValidator<CreateUserCommand>, CreateUserValidator>();
        services.AddRogue();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISender>());
        // Confirmed against a real thrown exception (not hand-guessed): "Cannot resolve scoped
        // service 'SkathIO.Rogue.ISender' from root provider." — the exact ASP.NET Core DI wording
        // for resolving a Scoped service from the root container.
        Assert.Equal("Cannot resolve scoped service 'SkathIO.Rogue.ISender' from root provider.", ex.Message);
    }
}
