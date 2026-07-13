using System;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using Xunit;

namespace SkathIO.Rogue.Integration.Tests;

/// <summary>
/// D1/D2's bare-Rogue twin of
/// <c>SkathIO.Rogue.DiResolution.Tests.DiResolutionMatrixTests.RootProviderDispatch_FluentValidationReferenced_ThrowsResolvingScopedSender</c>.
/// This project references only <see cref="SkathIO.Rogue"/> and the generator (no
/// <c>SkathIO.Rogue.Validation.FluentValidation</c> — see this project's <c>.csproj</c>), so it
/// proves, as an actual regression test rather than an inference from <c>design.md</c> §2's rows
/// I/J, that resolving a Scoped <see cref="ISender"/> from the root provider fails identically with
/// or without FluentValidation in the compilation — the failure is mediator-inherent (D2,
/// rogue-perf's deliberately-Scoped mediator/dispatcher), not FluentValidation-specific.
/// <br/><br/>
/// **Provider construction is deliberately different from <see cref="SmokePerfTests"/>
/// (`PerfGateTests.cs`)**: that suite's <c>AddRogue_WiresDispatch_NoBehaviors</c> builds a bare
/// <c>services.BuildServiceProvider()</c> (no <see cref="ServiceProviderOptions"/>), so scope
/// validation is off and resolving the Scoped <c>ISender</c> from the root succeeds there — it
/// proves the opposite (non-throwing, unvalidated) case and must stay untouched. This test opts
/// into <c>ValidateScopes = true</c> (not <c>ValidateOnBuild</c> — there is no captive-dependency-
/// at-build-time concern in the bare-Rogue case, only the resolve-from-root-at-runtime failure that
/// <c>ValidateScopes</c> alone catches) so the throw actually occurs.
/// </summary>
public sealed class RootScopeDispatchTests
{
    [Fact]
    public void RootProviderDispatch_BareRogue_ThrowsResolvingScopedSender()
    {
        var services = new ServiceCollection();
        services.AddRogue();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // The throw fires at GetRequiredService<ISender>() resolution itself (ISender is
        // TryAddScoped — RogueServiceCollectionExtensions.cs:34), before any Send call. Confirmed
        // against a real thrown exception (not hand-guessed), and asserted byte-for-byte identical
        // to SkathIO.Rogue.DiResolution.Tests' FluentValidation-referenced variant — the same
        // equivalence check that pins D1's "mediator-inherent, not FluentValidation-specific" claim.
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISender>());
        Assert.Equal("Cannot resolve scoped service 'SkathIO.Rogue.ISender' from root provider.", ex.Message);
    }
}
