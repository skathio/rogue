using Microsoft.CodeAnalysis;

namespace SkathIO.Rogue.Jobs.Analyzers;

internal static class JobsDiagnostics
{
    private const string HelpLinkBase = "https://skathio.github.io/rogue/diagnostics/";
    private const string Category = "SkathIO.Rogue";

    /// <summary>The diagnostic property that carries the uncovered job type, fully qualified, for the code fix.</summary>
    internal const string JobTypeProperty = "JobType";

    /// <summary>
    /// ROGUE019 — a <c>[Job]</c> type is not covered by any <c>[JsonSerializable]</c> attribute on the
    /// project's <c>JsonSerializerContext</c>. Warning: enqueueing it fails at startup, when the job
    /// registry binds each command to its serializer.
    /// </summary>
    internal static readonly DiagnosticDescriptor JobNotSerializable = new DiagnosticDescriptor(
        id: "ROGUE019",
        title: "Job type is not covered by the JsonSerializerContext",
        messageFormat: "Job type '{0}' is not covered by the JsonSerializerContext '{1}'; add [JsonSerializable(typeof({0}))] to it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE019",
        customTags: WellKnownDiagnosticTags.CompilationEnd);
}
