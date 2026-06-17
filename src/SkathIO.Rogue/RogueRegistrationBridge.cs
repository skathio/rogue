namespace SkathIO.Rogue;

/// <summary>
/// Bridge between the DLL's <see cref="RogueServiceCollectionExtensions.AddRogue"/> and the
/// source-generated registration in the consumer's compilation. The generator wires its
/// registrar via a module initializer on net5+ (PD-15), calling <see cref="Register"/>.
/// <para>
/// This type and its <see cref="Register"/> method are <c>public</c> by necessity: the module
/// initializer that calls <see cref="Register"/> is emitted into the <em>consumer's</em>
/// compilation, which has no <c>InternalsVisibleTo</c> grant from this assembly. An
/// <c>internal</c> entry point would produce a CS0122 compile error in every real consumer — the
/// exact cross-assembly access failure PD-14/PD-15 exist to eliminate. It is not intended for
/// application code to call directly.
/// </para>
/// <para>
/// <b>Append-only registry (PD-33/PD-38).</b> Registrars are kept in an append-only,
/// order-preserving list rather than a single last-writer-wins slot. If multiple consuming
/// compilations in the same process each carry a generator-emitted module initializer, every
/// initializer's registrar is retained and <see cref="RogueServiceCollectionExtensions.AddRogue"/>
/// invokes them <em>all</em>, in append order, so no compilation's registrations are silently
/// clobbered. The single-compilation-per-process model Rogue targets is unaffected (one registrar,
/// invoked once). Appends are deduplicated by delegate identity and the list cannot be cleared or
/// redirected, only added to (PD-33 trust-boundary note).
/// </para>
/// </summary>
public static class RogueRegistrationBridge
{
    // PD-38: a lock-guarded, order-preserving List (not ConcurrentBag) so cross-registrar
    // IPipelineBehavior<,> order is deterministic and the obsolete getter can return a *defined*
    // value (the last appended registrar). Module initializers run during assembly load —
    // single-threaded per assembly, but multiple assemblies can load concurrently in some hosts —
    // so the append must be thread-safe. List + lock ship on every TFM (incl. netstandard2.0), so
    // no TFM split is needed.
    private static readonly object Gate = new object();

    private static readonly System.Collections.Generic.List<System.Action<
        Microsoft.Extensions.DependencyInjection.IServiceCollection,
        RogueOptions>> Registrars = new System.Collections.Generic.List<System.Action<
            Microsoft.Extensions.DependencyInjection.IServiceCollection,
            RogueOptions>>();

    /// <summary>
    /// Appends a generator-wired registrar to the append-only registry. Called once by each
    /// consumer-compilation module initializer (net5+, PD-15); on netstandard2.0 the consumer calls
    /// <c>RogueGeneratedRegistration.Register</c> explicitly. <see cref="RogueServiceCollectionExtensions.AddRogue"/>
    /// invokes every registered registrar, in append order. A <c>null</c> <paramref name="registrar"/>
    /// is ignored, and a registrar already present (by delegate identity) is not appended again
    /// (deduplication — a re-entrant or duplicate module-init append is a no-op). Not for direct
    /// application use.
    /// </summary>
    public static void Register(System.Action<
        Microsoft.Extensions.DependencyInjection.IServiceCollection,
        RogueOptions> registrar)
    {
        if (registrar is null)
        {
            return;
        }

        lock (Gate)
        {
            if (!Registrars.Contains(registrar))
            {
                Registrars.Add(registrar);
            }
        }
    }

    /// <summary>
    /// Generator-wired registrar compatibility shim. Superseded by <see cref="Register"/>: the
    /// setter <em>appends</em> to the append-only registry (ignoring <c>null</c>) and the getter
    /// returns the <em>last appended</em> registrar (or <c>null</c> when the registry is empty).
    /// Retained so older generator output and hand-written consumers that assign this field still
    /// contribute their registrar rather than breaking. Not for direct application use.
    /// </summary>
    [System.Obsolete(
        "Assign via Register(...); this setter now appends to an append-only registry. " +
        "Will be removed in a future major version.",
        error: false)]
    public static System.Action<
        Microsoft.Extensions.DependencyInjection.IServiceCollection,
        RogueOptions>? GeneratedRegistrar
    {
        get
        {
            lock (Gate)
            {
                return Registrars.Count == 0 ? null : Registrars[Registrars.Count - 1];
            }
        }
        set
        {
            // null is a defined no-op: do not append a null delegate and do not throw (security
            // Minor, PD-38). A non-null assignment routes through Register so the dedup + append
            // apply to it too.
            if (value is not null)
            {
                Register(value);
            }
        }
    }

    /// <summary>
    /// Snapshots all registered registrars in append order. <c>internal</c> — only
    /// <see cref="RogueServiceCollectionExtensions.AddRogue"/> (same assembly) needs the read path;
    /// the public surface is append-only. Test assemblies use this (plus
    /// <see cref="RestoreRegistrars"/>) to isolate the process-global registry between cases that
    /// load distinct generated assemblies.
    /// </summary>
    internal static System.Action<
        Microsoft.Extensions.DependencyInjection.IServiceCollection,
        RogueOptions>[] SnapshotRegistrars()
    {
        lock (Gate)
        {
            return Registrars.ToArray();
        }
    }

    /// <summary>
    /// Replaces the registry contents with <paramref name="registrars"/> (in order).
    /// <c>internal</c>, test-only: the public surface stays append-only (no clear/redirect). Used to
    /// restore the process-global registry after a test temporarily isolates it. Not reachable from
    /// application or generated code (no <c>InternalsVisibleTo</c> grant to consumer compilations).
    /// </summary>
    internal static void RestoreRegistrars(System.Action<
        Microsoft.Extensions.DependencyInjection.IServiceCollection,
        RogueOptions>[] registrars)
    {
        lock (Gate)
        {
            Registrars.Clear();
            Registrars.AddRange(registrars);
        }
    }
}
