using Microsoft.CodeAnalysis;

namespace SkathIO.Rogue.SourceGenerator;

internal static class DiagnosticDescriptors
{
    private const string HelpLinkBase = "https://skathio.github.io/rogue/diagnostics/";
    private const string Category = "SkathIO.Rogue";

    /// <summary>ROGUE001 — No handler found for request type.</summary>
    internal static readonly DiagnosticDescriptor NoHandler = new DiagnosticDescriptor(
        id: "ROGUE001",
        title: "No handler registered for request type",
        messageFormat: "Request type '{0}' has no registered handler. Add an 'IRequestHandler<{0}, ...>' implementation.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE001");

    /// <summary>ROGUE002 — Multiple handlers found for request type.</summary>
    internal static readonly DiagnosticDescriptor DuplicateHandler = new DiagnosticDescriptor(
        id: "ROGUE002",
        title: "Multiple handlers registered for request type",
        messageFormat: "Request type '{0}' has more than one handler: {1}. A request must have exactly one handler.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE002");

    /// <summary>ROGUE003 — Handler response type mismatch.</summary>
    internal static readonly DiagnosticDescriptor ResponseTypeMismatch = new DiagnosticDescriptor(
        id: "ROGUE003",
        title: "Handler response type does not match request",
        messageFormat: "Handler '{0}' declares response type '{1}' but request '{2}' expects '{3}'. Align the handler's response type with 'IRequest<...>'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE003");

    /// <summary>ROGUE004 — Handler has unresolvable constructor dependency (best-effort; handlers only in v1).</summary>
    internal static readonly DiagnosticDescriptor UnconstructableType = new DiagnosticDescriptor(
        id: "ROGUE004",
        title: "Handler may not be constructable",
        messageFormat: "'{0}' has constructor parameter '{1}' of type '{2}' which does not appear to be registered. Register '{2}' in DI.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE004");

    /// <summary>ROGUE005 — Handler/behavior type is abstract or has no usable public constructor.</summary>
    internal static readonly DiagnosticDescriptor AbstractOrNoUsableCtor = new DiagnosticDescriptor(
        id: "ROGUE005",
        title: "Handler or behavior is abstract or has no public constructor",
        messageFormat: "'{0}' is abstract or has no public constructor and cannot be registered by the generator",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE005");

    /// <summary>ROGUE006 — Open-generic request type (not supported by generator).</summary>
    internal static readonly DiagnosticDescriptor OpenGenericRequest = new DiagnosticDescriptor(
        id: "ROGUE006",
        title: "Open-generic request type not supported by generator",
        messageFormat: "Request type '{0}' is an open generic type. The generator cannot emit a dispatch path for open-generic requests. Use the 'SkathIO.Rogue.MediatR' escape hatch (not AOT-safe) for this pattern.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE006");

    /// <summary>ROGUE010 — IMediator injected where ISender/IPublisher would suffice (opt-in).</summary>
    internal static readonly DiagnosticDescriptor IMediatorNudge = new DiagnosticDescriptor(
        id: "ROGUE010",
        title: "Consider injecting ISender or IPublisher instead of IMediator",
        messageFormat: "'{0}' injects 'IMediator' but only uses '{1}' capabilities. Prefer injecting 'ISender' or 'IPublisher' to express the narrowest dependency.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,  // Info severity; suppress via editorconfig if not wanted
        helpLinkUri: HelpLinkBase + "ROGUE010");
}
