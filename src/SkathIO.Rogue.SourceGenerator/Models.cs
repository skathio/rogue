namespace SkathIO.Rogue.SourceGenerator;

/// <summary>Accessibility of the discovered type.</summary>
internal enum TypeAccessibility { Public, Internal, Other }

/// <summary>Handler for a request (IRequestHandler&lt;,&gt; or semantic aliases).</summary>
internal sealed record HandlerModel(
    string TypeFqn,
    string RequestFqn,
    string? ResponseFqn,        // null for void-path handlers (IRequestHandler<T>)
    TypeAccessibility Accessibility,
    EquatableArray<string> CtorArgTypeFqns,
    bool IsAbstract,            // true if the type is abstract (ROGUE005)
    bool HasPublicCtor          // false if no usable public constructor exists (ROGUE005)
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

/// <summary>Notification handler (INotificationHandler&lt;T&gt;).</summary>
internal sealed record NotificationHandlerModel(
    string TypeFqn,
    string NotificationFqn,
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

/// <summary>Streaming request handler (IStreamRequestHandler&lt;,&gt;).</summary>
internal sealed record StreamHandlerModel(
    string TypeFqn,
    string RequestFqn,
    string ResponseElementFqn,
    TypeAccessibility Accessibility
);

/// <summary>
/// A request message type (implements IRequest&lt;T&gt;, INotification, IStreamRequest, etc.).
/// Discovered so the generator can cross-check against handlers (ROGUE001).
/// </summary>
internal sealed record RequestMessageModel(
    string TypeFqn,
    string? ResponseFqn,    // null for INotification and IRequest (no-response)
    bool IsOpenGeneric,     // true if the type itself has type parameters (ROGUE006)
    bool IsNotification,
    bool IsStream
);

/// <summary>All discovered models from one compilation run.</summary>
internal sealed record DiscoveredModels(
    EquatableArray<HandlerModel> Handlers,
    EquatableArray<BehaviorModel> Behaviors,
    EquatableArray<NotificationHandlerModel> NotificationHandlers,
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
    internal sealed record NotificationHandler(NotificationHandlerModel Model) : DiscoveredItem;
    internal sealed record Processor(ProcessorModel Model) : DiscoveredItem;
    internal sealed record StreamHandler(StreamHandlerModel Model) : DiscoveredItem;
    internal sealed record RequestMessage(RequestMessageModel Model) : DiscoveredItem;
}
