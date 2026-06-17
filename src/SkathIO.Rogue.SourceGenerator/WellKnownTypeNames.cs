namespace SkathIO.Rogue.SourceGenerator;

internal static class WellKnownTypeNames
{
    // Root namespace
    public const string RootNamespace = "SkathIO.Rogue";

    // ── Primary CQS handler interfaces (PD-40 clean break — discovered independently, no shared marker)
    // FQN with backtick-arity notation used in Roslyn metadata names.
    public const string ICommandHandler2 = "SkathIO.Rogue.ICommandHandler`2";
    public const string ICommandHandler1 = "SkathIO.Rogue.ICommandHandler`1";
    public const string IQueryHandler2   = "SkathIO.Rogue.IQueryHandler`2";

    // ── Primary event handler (PD-42 — renamed from INotificationHandler)
    public const string IEventHandler1 = "SkathIO.Rogue.IEventHandler`1";

    // ── Primary streaming (net8+ only, but the generator may encounter them in any TFM)
    public const string IStreamQueryHandler2 = "SkathIO.Rogue.IStreamQueryHandler`2";
    public const string IStreamQuery1        = "SkathIO.Rogue.IStreamQuery`1";

    // ── Primary CQS message markers (PD-40 clean break — the three independent marker families)
    public const string ICommand1 = "SkathIO.Rogue.ICommand`1";   // ICommand<TResponse>
    public const string ICommand  = "SkathIO.Rogue.ICommand";     // ICommand (no response)
    public const string IQuery1   = "SkathIO.Rogue.IQuery`1";     // IQuery<TResponse>

    // ── Primary event marker (PD-42)
    public const string IEvent = "SkathIO.Rogue.IEvent";

    // Behaviors (mechanism-unchanged — keyed on the message/item type pair, not a deleted marker)
    public const string IPipelineBehavior2       = "SkathIO.Rogue.IPipelineBehavior`2";
    public const string IStreamPipelineBehavior2 = "SkathIO.Rogue.IStreamPipelineBehavior`2";

    // Behavior order attribute (PD-4)
    public const string BehaviorOrderAttribute   = "SkathIO.Rogue.BehaviorOrderAttribute";

    // Processors (mechanism-unchanged — constrained on notnull)
    public const string IRequestPreProcessor1    = "SkathIO.Rogue.IRequestPreProcessor`1";
    public const string IRequestPostProcessor2   = "SkathIO.Rogue.IRequestPostProcessor`2";
    public const string IRequestExceptionHandler3 = "SkathIO.Rogue.IRequestExceptionHandler`3";
    public const string IRequestExceptionAction2  = "SkathIO.Rogue.IRequestExceptionAction`2";

    // Entry-point interfaces (for ROGUE010 nudge)
    public const string IMediator  = "SkathIO.Rogue.IMediator";
    public const string ISender    = "SkathIO.Rogue.ISender";
    public const string IPublisher = "SkathIO.Rogue.IPublisher";

    // ── Adapter-mapping discovery path (PD-43 / PD-48): the MediatR-shaped IRequest surface lives ONLY in
    // the SkathIO.Rogue.MediatR adapter (namespace SkathIO.Rogue.Compatibility). The core declares none of
    // these types (PD-40 clean break).
    //
    // PD-48 split: only the REQUEST family is self-contained and needs a dedicated discovery rule (the F8
    // command-vs-query fork can't be expressed as core inheritance). The AdapterIRequestHandler2/1
    // constants drive ExtractFromSymbol's adapter-mapping branches (default → ICommand<T>, [MapAsQuery] →
    // IQuery<T>, no-response → void ICommand, [MapAsQuery]+no-response → ROGUE012). AdapterIRequest1/
    // AdapterIRequest are kept for documentation of the F8 mapping target shapes (the discovery keys on the
    // HANDLER interface, where [MapAsQuery] is read off the request type argument).
    public const string AdapterIRequest1    = "SkathIO.Rogue.Compatibility.IRequest`1";  // IRequest<TResponse>
    public const string AdapterIRequest     = "SkathIO.Rogue.Compatibility.IRequest";    // IRequest (no response)
    public const string AdapterIRequestHandler2 = "SkathIO.Rogue.Compatibility.IRequestHandler`2";
    public const string AdapterIRequestHandler1 = "SkathIO.Rogue.Compatibility.IRequestHandler`1";

    // PD-48: the NOTIFICATION and STREAMING adapter families are thin IS-A sub-interfaces of the core
    // (Compatibility.INotification : IEvent, INotificationHandler<T> : IEventHandler<T>,
    // IStreamRequest<T> : IStreamQuery<T>, IStreamRequestHandler<,> : IStreamQueryHandler<,>). They are
    // therefore discovered TRANSITIVELY by the existing IEventHandler1 / IStreamQueryHandler2 branches —
    // no generator branch keys on these constants. They are kept as cheap documentation of the F8 mapping
    // table (intentionally unused — see PD-48).
    public const string AdapterINotification = "SkathIO.Rogue.Compatibility.INotification";
    public const string AdapterINotificationHandler1 = "SkathIO.Rogue.Compatibility.INotificationHandler`1";
    public const string AdapterIStreamRequest1 = "SkathIO.Rogue.Compatibility.IStreamRequest`1";
    public const string AdapterIStreamRequestHandler2 = "SkathIO.Rogue.Compatibility.IStreamRequestHandler`2";

    // Adapter query-override marker (PD-43 amendment / PD-48): [MapAsQuery] on an adapter IRequest type
    // maps the discovered handler to IQuery<TResponse> instead of the F8 default ICommand<TResponse>.
    // Read via ExtractFromSymbol's HasMapAsQueryAttribute (same GetAttributes() scan as [BehaviorOrder]).
    public const string MapAsQueryAttribute = "SkathIO.Rogue.MediatR.MapAsQueryAttribute";
}
