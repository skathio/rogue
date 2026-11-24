using Microsoft.CodeAnalysis;

namespace SkathIO.Rogue.SourceGenerator;

internal static class DiagnosticDescriptors
{
    private const string HelpLinkBase = "https://skathio.github.io/rogue/diagnostics/";
    private const string Category = "SkathIO.Rogue";

    /// <summary>ROGUE001 — No handler found for command/query type.</summary>
    internal static readonly DiagnosticDescriptor NoHandler = new DiagnosticDescriptor(
        id: "ROGUE001",
        title: "No handler registered for command/query type",
        messageFormat: "Message type '{0}' has no registered handler. Add an 'ICommandHandler'/'IQueryHandler' implementation for it.",
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
        messageFormat: "Handler '{0}' declares response type '{1}' but message '{2}' expects '{3}'. Align the handler's response type with the command/query's response.",
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

    /// <summary>
    /// ROGUE011 — Type implements multiple CQS contracts. With no shared base between the families, a type
    /// that is both a command and a query (or otherwise implements more than one of
    /// ICommand/IQuery/IEvent/IStreamQuery) is ambiguous — there is no shared marker to disambiguate
    /// the dispatch path, so the generator cannot emit one. Error: split the type into separate
    /// command and query messages. (ROGUE007 is intentionally not reused — removed-from-scope id.)
    /// </summary>
    internal static readonly DiagnosticDescriptor MultipleCqsContracts = new DiagnosticDescriptor(
        id: "ROGUE011",
        title: "Type implements multiple CQS contracts",
        messageFormat: "Type '{0}' implements more than one CQS contract (combinations of ICommand/IQuery/IEvent/IStreamQuery). A message must belong to exactly one family; split it into separate command and query types.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE011");

    /// <summary>
    /// ROGUE012 — MediatR-adapter command-vs-query mapping conflict. The
    /// adapter-mapping rule maps an adapter <c>IRequest&lt;T&gt;</c> to <c>ICommand&lt;T&gt;</c> by
    /// default and to <c>IQuery&lt;T&gt;</c> when the request carries <c>[MapAsQuery]</c>. Applying
    /// <c>[MapAsQuery]</c> to a <em>no-response</em> adapter <c>IRequest</c> is a conflict — a query must
    /// return a value — so the mapping cannot be resolved as requested. Warning (manual-review): the
    /// handler is still mapped to a void command (never silently mis-mapped or dropped); the author
    /// should either give the request a response type or remove <c>[MapAsQuery]</c>. The id reserves the
    /// same diagnostic for any future config-vs-attribute mapping disagreement. (ROGUE007 is intentionally
    /// not reused — removed-from-scope id.)
    /// </summary>
    internal static readonly DiagnosticDescriptor AdapterMappingConflict = new DiagnosticDescriptor(
        id: "ROGUE012",
        title: "Adapter command-vs-query mapping conflict",
        messageFormat: "Adapter request type '{0}' is marked [MapAsQuery] but has no response type. A query must return a value. Give the request a response type (IRequest<TResponse>) or remove [MapAsQuery]; it is mapped as a void command in the meantime.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE012");

    /// <summary>
    /// ROGUE013 — a referenced feature package (SkathIO.Rogue.Validation, .Jobs or .Messaging) has a
    /// different major.minor version than the core SkathIO.Rogue package. The feature packages carry
    /// no generator of their own: the core generator emits their code, so it assumes the feature
    /// package's types and contracts match its own release. Warning: the build may still succeed, but
    /// the generated code can call members the referenced feature package does not have.
    /// </summary>
    internal static readonly DiagnosticDescriptor FeaturePackageVersionMismatch = new DiagnosticDescriptor(
        id: "ROGUE013",
        title: "Feature package version differs from the core package",
        messageFormat: "Package '{0}' is version {1}, but the core SkathIO.Rogue package is version {2}. The core source generator emits the code for '{0}', so reference the same major.minor version of both packages.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE013");

    /// <summary>
    /// ROGUE014 — a <c>[Validate]</c> type, or a type that contains it, is not declared <c>partial</c>.
    /// The validator is generated into the type (so it can call the type's private <c>Rules</c> hook),
    /// which needs every declaration in the chain to be partial. Error: the type would go unvalidated.
    /// </summary>
    internal static readonly DiagnosticDescriptor ValidatedTypeNotPartial = new DiagnosticDescriptor(
        id: "ROGUE014",
        title: "[Validate] type must be partial",
        messageFormat: "'{0}' has [Validate], but it or a type that contains it is not declared partial, so its validator cannot be generated",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE014");

    /// <summary>
    /// ROGUE015 — a validation rule attribute cannot check the property it is placed on: no public static
    /// <c>IsValid</c> overload takes the property's type with the attribute's arguments, or the rule lacks
    /// its <c>DefaultCode</c>/<c>DefaultMessage</c> members. Error: reported here instead of as a compile
    /// error inside generated code.
    /// </summary>
    internal static readonly DiagnosticDescriptor RuleDoesNotFitProperty = new DiagnosticDescriptor(
        id: "ROGUE015",
        title: "Validation rule does not fit the property",
        messageFormat: "Rule '{0}' cannot check '{1}': {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE015");

    /// <summary>
    /// ROGUE016 — a <c>[Validate]</c> type declares a method named <c>Rules</c> whose signature is not the
    /// hook's. Error: the hook would silently never run.
    /// </summary>
    internal static readonly DiagnosticDescriptor RulesHookSignature = new DiagnosticDescriptor(
        id: "ROGUE016",
        title: "Rules hook has the wrong signature",
        messageFormat: "'{0}' declares a Rules method that is not the validation hook; declare it as 'static void Rules({1} x, ref ValidationErrors errors)'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE016");

    /// <summary>
    /// ROGUE017 — a <c>[Validate]</c> type from a referenced assembly carries no generated validator,
    /// because that assembly was built without the SkathIO.Rogue generator. Error: the request or nested
    /// value would otherwise go unchecked without a word.
    /// </summary>
    internal static readonly DiagnosticDescriptor ValidatorMissing = new DiagnosticDescriptor(
        id: "ROGUE017",
        title: "Validated type has no generated validator",
        messageFormat: "'{0}' has [Validate], but its assembly was built without the SkathIO.Rogue source generator, so '{1}' cannot be validated",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE017");

    /// <summary>
    /// ROGUE018 — validation rules (rule attributes or an <c>IAsyncRules&lt;T&gt;</c> class) are declared for
    /// a type that has no <c>[Validate]</c> attribute, so they never run. Warning: the build still works,
    /// but the rules are dead.
    /// </summary>
    internal static readonly DiagnosticDescriptor RulesNeverRun = new DiagnosticDescriptor(
        id: "ROGUE018",
        title: "Validation rules never run",
        messageFormat: "Validation rules are declared for '{0}', but it has no [Validate] attribute, so they never run",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "ROGUE018");
}
