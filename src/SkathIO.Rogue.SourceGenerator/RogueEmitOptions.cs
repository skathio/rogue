namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// Controls what the emitters produce. Phase 4.1: all flags are static defaults.
/// Future phases add build-property support.
/// </summary>
internal sealed class RogueEmitOptions
{
    /// <summary>
    /// Historical static gate for the object-typed <c>SendObject</c> dispatch switch.
    /// As of Fix 2 (PD-3) the <c>SendObject</c> switch is ALWAYS emitted when handlers exist —
    /// the runtime <c>RogueOptions.EnableObjectDispatch</c> flag controls whether the object path
    /// is used, not whether it is generated. This flag is retained for a future Phase 8 packaging
    /// gate (linker trim/substitution) and is currently unused by the emitters.
    /// </summary>
    internal bool EnableObjectDispatch { get; init; } = false;
}
