using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Tests;

/// <summary>
/// Exercises the append-only registrar registry (PD-33/PD-38) through its public surface
/// (<see cref="RogueRegistrationBridge.Register"/>, the <c>[Obsolete]</c>
/// <see cref="RogueRegistrationBridge.GeneratedRegistrar"/> shim) and the real
/// <see cref="RogueServiceCollectionExtensions.AddRogue"/> invoke-all path. No mocks: a real
/// <see cref="ServiceCollection"/> is built and the registrars' side effects on it are asserted.
///
/// The registry is a process-global static, so every test isolates it by snapshotting the current
/// contents (via the internal <c>SnapshotRegistrars</c>, visible through InternalsVisibleTo), clearing
/// to a known-empty baseline with <c>RestoreRegistrars</c>, running the case, then restoring the
/// snapshot in a finally — so tests neither see nor leak each other's (or the harness's) registrars.
/// </summary>
public sealed class RogueRegistrationBridgeTests
{
    [Fact]
    public void Register_TwoDistinctRegistrars_BothInvokedByAddRogue_InAppendOrder()
    {
        var saved = RogueRegistrationBridge.SnapshotRegistrars();
        try
        {
            RogueRegistrationBridge.RestoreRegistrars(Array.Empty<Action<IServiceCollection, RogueOptions>>());

            RogueRegistrationBridge.Register((svc, _) => svc.AddSingleton(new MarkerA()));
            RogueRegistrationBridge.Register((svc, _) => svc.AddSingleton(new MarkerB()));

            var services = new ServiceCollection();
            services.AddRogue();

            // Both registrars were applied — last-writer-wins would have dropped MarkerA.
            int idxA = services.ToList().FindIndex(d => d.ServiceType == typeof(MarkerA));
            int idxB = services.ToList().FindIndex(d => d.ServiceType == typeof(MarkerB));
            Assert.True(idxA >= 0, "MarkerA registrar did not run");
            Assert.True(idxB >= 0, "MarkerB registrar did not run");

            // Append order is preserved: MarkerA's descriptor precedes MarkerB's.
            Assert.True(idxA < idxB, "registrars applied out of append order");
        }
        finally
        {
            RogueRegistrationBridge.RestoreRegistrars(saved);
        }
    }

    [Fact]
    public void GeneratedRegistrar_Setter_Appends_AndDedupsByDelegateIdentity()
    {
        var saved = RogueRegistrationBridge.SnapshotRegistrars();
        try
        {
            RogueRegistrationBridge.RestoreRegistrars(Array.Empty<Action<IServiceCollection, RogueOptions>>());

            int before = RogueRegistrationBridge.SnapshotRegistrars().Length;
            Action<IServiceCollection, RogueOptions> registrar = (svc, _) => svc.AddSingleton(new MarkerA());

#pragma warning disable CS0618 // GeneratedRegistrar is the [Obsolete] compat shim under test.
            RogueRegistrationBridge.GeneratedRegistrar = registrar;
            Assert.Equal(before + 1, RogueRegistrationBridge.SnapshotRegistrars().Length);

            // The getter returns the last appended registrar.
            Assert.Same(registrar, RogueRegistrationBridge.GeneratedRegistrar);

            // Re-appending the same delegate identity is a no-op (dedup) — size unchanged.
            RogueRegistrationBridge.GeneratedRegistrar = registrar;
            Assert.Equal(before + 1, RogueRegistrationBridge.SnapshotRegistrars().Length);
#pragma warning restore CS0618
        }
        finally
        {
            RogueRegistrationBridge.RestoreRegistrars(saved);
        }
    }

    [Fact]
    public void GeneratedRegistrar_SetNull_IsNoOp_AndAddRogueDoesNotThrow()
    {
        var saved = RogueRegistrationBridge.SnapshotRegistrars();
        try
        {
            RogueRegistrationBridge.RestoreRegistrars(Array.Empty<Action<IServiceCollection, RogueOptions>>());
            int before = RogueRegistrationBridge.SnapshotRegistrars().Length;

#pragma warning disable CS0618 // GeneratedRegistrar is the [Obsolete] compat shim under test.
            RogueRegistrationBridge.GeneratedRegistrar = null;
#pragma warning restore CS0618

            // null assignment appends nothing — registry size unchanged.
            Assert.Equal(before, RogueRegistrationBridge.SnapshotRegistrars().Length);

            // And AddRogue() over an empty registry is well-defined (no NRE from the invoke-all loop).
            var services = new ServiceCollection();
            var ex = Record.Exception(() => services.AddRogue());
            Assert.Null(ex);
        }
        finally
        {
            RogueRegistrationBridge.RestoreRegistrars(saved);
        }
    }

    private sealed class MarkerA { }

    private sealed class MarkerB { }
}
