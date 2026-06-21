using Microsoft.CodeAnalysis;

namespace SkathIO.Rogue.Migration.Analyzer;

internal static class MigrationDiagnostics
{
    private const string HelpLinkBase = "https://skathio.github.io/rogue/migration/";

    public static readonly DiagnosticDescriptor UsingMediatR = new(
        id: "ROGM001",
        title: "Replace MediatR using directive",
        messageFormat: "Replace 'using MediatR;' with 'using SkathIO.Rogue;'",
        category: "Migration",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGM001");

    public static readonly DiagnosticDescriptor TaskReturnType = new(
        id: "ROGM002",
        title: "Replace Task return type with ValueTask",
        messageFormat: "Handler '{0}' returns Task — replace with ValueTask (Rogue uses ValueTask throughout)",
        category: "Migration",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGM002");

    public static readonly DiagnosticDescriptor OpenGenericRequest = new(
        id: "ROGM003",
        title: "Open-generic request requires manual migration",
        messageFormat: "'{0}' is an open-generic request type — the source generator cannot handle it. Use the ReflectionMediator escape hatch in SkathIO.Rogue.MediatR (not AOT-safe) or restructure to closed generic.",
        category: "Migration",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGM003");

    public static readonly DiagnosticDescriptor AddMediatRCall = new(
        id: "ROGM004",
        title: "AddMediatR is forwarded to AddRogue",
        messageFormat: "AddMediatR forwards to AddRogue via the compat shim. Consider switching to AddRogue() directly for clarity.",
        category: "Migration",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGM004");

    public static readonly DiagnosticDescriptor AmbiguousCommandOrQuery = new(
        id: "ROGM005",
        title: "Ambiguous command-vs-query intent — migrated to ICommand, review manually",
        messageFormat: "'{0}' is a response-bearing MediatR request whose command-vs-query intent could not be inferred from its name. It was migrated to the safe default ICommand<T>. If it only reads state, change it to IQuery<T> (and its handler to IQueryHandler<,>) — never silently mis-mapped.",
        category: "Migration",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGM005");

    public static readonly DiagnosticDescriptor MediatRMarkerType = new(
        id: "ROGM006",
        title: "Migrate MediatR marker/handler interface to the CQS contract",
        messageFormat: "'{0}' implements the MediatR-shaped '{1}' — migrate it to the SkathIO.Rogue CQS contract (ICommand/IQuery/IEvent + handler)",
        category: "Migration",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGM006");
}
