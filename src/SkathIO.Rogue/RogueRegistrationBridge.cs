namespace SkathIO.Rogue;

/// <summary>
/// Bridge between the DLL's <see cref="RogueServiceCollectionExtensions.AddRogue"/> and the
/// source-generated registration in the consumer's compilation. The generator wires
/// <see cref="GeneratedRegistrar"/> via a module initializer on net5+ (PD-15).
/// <para>
/// This type and its field are <c>public</c> by necessity: the module initializer that assigns
/// <see cref="GeneratedRegistrar"/> is emitted into the <em>consumer's</em> compilation, which has
/// no <c>InternalsVisibleTo</c> grant from this assembly. An <c>internal</c> field would produce a
/// CS0122 compile error in every real consumer — the exact cross-assembly access failure PD-14/PD-15
/// exist to eliminate. It is not intended for application code to read or write directly.
/// </para>
/// </summary>
public static class RogueRegistrationBridge
{
    /// <summary>
    /// Generator-wired registrar. Assigned once by the consumer-compilation module initializer
    /// (net5+) and invoked by <see cref="RogueServiceCollectionExtensions.AddRogue"/>. Not for
    /// direct application use.
    /// <para>
    /// <b>Exactly one generator-wired compilation per process (last-writer-wins).</b> This field is a
    /// single static slot. If multiple consuming compilations in the same process each carry
    /// generator-emitted module initializers that assign <see cref="GeneratedRegistrar"/>, the last
    /// initializer to run wins — module-initializer ordering across assemblies is not guaranteed by
    /// the runtime — silently clobbering the registrations of the others. This is acceptable for the
    /// single-compilation-per-process model Rogue targets (one application, one generator run), but
    /// would be a footgun in a multi-assembly-with-generators-per-assembly scenario. This is the source
    /// of the release-readiness review's "last-writer-wins clobber" finding.
    /// </para>
    /// </summary>
    public static System.Action<
        Microsoft.Extensions.DependencyInjection.IServiceCollection,
        RogueOptions>? GeneratedRegistrar;
}
