namespace SkathIO.Rogue.SourceGenerator;

/// <summary>Accessibility of the discovered type.</summary>
internal enum TypeAccessibility { Public, Internal, Other }

/// <summary>
/// Whether a discovered request handler handles a command or a query. Under the CQS clean break
/// (PD-40) command and query are independent contracts (<c>ICommandHandler&lt;,&gt;</c> /
/// <c>IQueryHandler&lt;,&gt;</c>) with no shared marker; the kind is recorded so the dispatcher can
/// switch the typed reference (<c>ICommand&lt;T&gt;</c> vs <c>IQuery&lt;T&gt;</c>) it emits per handler.
/// </summary>
internal enum HandlerKind { Command, Query }

/// <summary>Handler for a command or query (ICommandHandler&lt;,&gt; / ICommandHandler&lt;&gt; / IQueryHandler&lt;,&gt;).</summary>
internal sealed record HandlerModel(
    string TypeFqn,
    string RequestFqn,
    string? ResponseFqn,        // null for void-path handlers (ICommandHandler<TCommand>)
    HandlerKind Kind,           // command vs query — drives the emitted typed reference (PD-40)
    TypeAccessibility Accessibility,
    EquatableArray<string> CtorArgTypeFqns,
    bool IsAbstract,            // true if the type is abstract (ROGUE005)
    bool HasPublicCtor,         // false if no usable public constructor exists (ROGUE005)
    /// <summary>
    /// True when this handler was discovered through the MediatR-adapter mapping rule (PD-48): it
    /// implements the adapter's <c>SkathIO.Rogue.Compatibility.IRequestHandler&lt;,&gt;</c> /
    /// <c>IRequestHandler&lt;&gt;</c> rather than a core <c>ICommandHandler</c>/<c>IQueryHandler</c>.
    /// The F8 convention has already been applied to set <see cref="Kind"/> (default → Command,
    /// <c>[MapAsQuery]</c> → Query). Adapter-mapped handlers register/resolve against the adapter
    /// handler interface (not the core CQS one) and dispatch ONLY through the object-dispatch path —
    /// the adapter message does not implement the core <c>ICommand&lt;T&gt;</c>/<c>IQuery&lt;T&gt;</c>
    /// markers, so a typed-<c>Send</c> switch <c>case</c> for it would not compile.
    /// </summary>
    bool IsAdapterMapped = false,
    /// <summary>
    /// True only for a no-response adapter handler (<c>IRequestHandler&lt;TReq&gt;</c>) whose request
    /// type carries <c>[MapAsQuery]</c> — the ROGUE012 conflict (a query must return a value, PD-43
    /// amendment / PD-48). The handler is still mapped to a void <c>ICommand</c> (not silently dropped);
    /// <c>EmitDiagnostics</c> reads this flag to raise ROGUE012 for manual review.
    /// </summary>
    bool MapAsQueryConflict = false
);

/// <summary>Pipeline behavior (IPipelineBehavior&lt;,&gt; or IStreamPipelineBehavior&lt;,&gt;).</summary>
internal sealed record BehaviorModel(
    string TypeFqn,
    bool IsOpen,                // true if the type itself is generic (open generic behavior)
    bool IsStream,
    bool IsAbstract,            // true if the type is abstract (ROGUE005)
    bool HasPublicCtor,         // false if no usable public constructor exists (ROGUE005)
    /// <summary>
    /// Unbound type FQN without type-parameter names (e.g. "MyApp.LoggingBehavior").
    /// For open behaviors: the unbound name used to emit closed constructions per handler.
    /// For closed behaviors: same as <see cref="TypeFqn"/>.
    /// </summary>
    string UnboundTypeFqn,
    /// <summary>Non-null for closed (<see cref="IsOpen"/>==false) behaviors: the request FQN from the implemented interface.</summary>
    string? ClosedRequestFqn,
    /// <summary>Non-null for closed (<see cref="IsOpen"/>==false) non-stream behaviors: the response FQN from the implemented interface.</summary>
    string? ClosedResponseFqn,
    /// <summary>
    /// The <c>[BehaviorOrder(int)]</c> value (PD-4). Defaults to 0 when the attribute is absent.
    /// Lower = outermost behavior (woven further out, executed first) per PD-13a.
    /// </summary>
    int Order = 0,
    /// <summary>
    /// True when discovered from a referenced assembly's metadata (PD-17), false when from the
    /// current compilation's source. Used as the PD-13a tie-break: source before metadata.
    /// </summary>
    bool IsMetadata = false
);

/// <summary>Event handler (IEventHandler&lt;T&gt;).</summary>
internal sealed record EventHandlerModel(
    string TypeFqn,
    string EventFqn,
    TypeAccessibility Accessibility
);

/// <summary>Pre/post processor or exception handler/action.</summary>
internal enum ProcessorKind { Pre, Post, ExceptionHandler, ExceptionAction }

/// <summary>Pre/post processor or exception handler/action model.</summary>
internal sealed record ProcessorModel(
    string TypeFqn,
    ProcessorKind Kind,
    string RequestFqn,
    string? ResponseFqn,
    string? ExceptionFqn,       // non-null for ExceptionHandler/ExceptionAction
    TypeAccessibility Accessibility
);

/// <summary>Streaming query handler (IStreamQueryHandler&lt;,&gt;).</summary>
internal sealed record StreamHandlerModel(
    string TypeFqn,
    string RequestFqn,
    string ResponseElementFqn,
    TypeAccessibility Accessibility
);

/// <summary>
/// The CQS family a discovered message type belongs to (PD-40 clean break — independent marker
/// families, no shared root). A message is a command (write), a query (read), an event (zero-or-more
/// handlers), or a streaming query (read, yields a sequence). <see cref="MultipleCqsContracts"/> marks
/// a type that implements more than one CQS family — ambiguous under the clean break (ROGUE011).
/// </summary>
internal enum MessageKind { Command, Query, Event, StreamQuery, MultipleCqsContracts }

/// <summary>
/// A message type (implements ICommand/ICommand&lt;T&gt;, IQuery&lt;T&gt;, IEvent, or
/// IStreamQuery&lt;T&gt;). Discovered so the generator can cross-check against handlers (ROGUE001) and
/// detect a type implementing multiple CQS contracts (ROGUE011).
/// </summary>
internal sealed record RequestMessageModel(
    string TypeFqn,
    string? ResponseFqn,    // null for IEvent and ICommand (no-response)
    bool IsOpenGeneric,     // true if the type itself has type parameters (ROGUE006)
    MessageKind Kind
);

/// <summary>All discovered models from one compilation run.</summary>
internal sealed record DiscoveredModels(
    EquatableArray<HandlerModel> Handlers,
    EquatableArray<BehaviorModel> Behaviors,
    EquatableArray<EventHandlerModel> EventHandlers,
    EquatableArray<ProcessorModel> Processors,
    EquatableArray<StreamHandlerModel> StreamHandlers,
    EquatableArray<RequestMessageModel> RequestMessages
);

/// <summary>
/// Sealed union of all discoverable item types. Used as the typed pipeline element
/// so the incremental framework can apply structural equality per item type, rather
/// than boxing models behind <c>object?</c>.
/// </summary>
internal abstract record DiscoveredItem
{
    private DiscoveredItem() { }  // prevent external subclassing

    internal sealed record Handler(HandlerModel Model) : DiscoveredItem;
    internal sealed record Behavior(BehaviorModel Model) : DiscoveredItem;
    internal sealed record EventHandler(EventHandlerModel Model) : DiscoveredItem;
    internal sealed record Processor(ProcessorModel Model) : DiscoveredItem;
    internal sealed record StreamHandler(StreamHandlerModel Model) : DiscoveredItem;
    internal sealed record RequestMessage(RequestMessageModel Model) : DiscoveredItem;
}
