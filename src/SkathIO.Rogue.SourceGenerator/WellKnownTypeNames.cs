namespace SkathIO.Rogue.SourceGenerator;

internal static class WellKnownTypeNames
{
    // Root namespace
    public const string RootNamespace = "SkathIO.Rogue";

    // Request handler interfaces (FQN with backtick-arity notation used in Roslyn metadata names)
    public const string IRequestHandler2 = "SkathIO.Rogue.IRequestHandler`2";
    public const string IRequestHandler1 = "SkathIO.Rogue.IRequestHandler`1";
    public const string ICommandHandler2 = "SkathIO.Rogue.ICommandHandler`2";
    public const string ICommandHandler1 = "SkathIO.Rogue.ICommandHandler`1";
    public const string IQueryHandler2   = "SkathIO.Rogue.IQueryHandler`2";

    // Notification
    public const string INotificationHandler1 = "SkathIO.Rogue.INotificationHandler`1";

    // Streaming (only net8+ but generator may encounter them in any TFM)
    public const string IStreamRequestHandler2  = "SkathIO.Rogue.IStreamRequestHandler`2";
    public const string IBaseStreamRequest      = "SkathIO.Rogue.IBaseStreamRequest";
    public const string IStreamRequest1         = "SkathIO.Rogue.IStreamRequest`1";

    // Behaviors
    public const string IPipelineBehavior2       = "SkathIO.Rogue.IPipelineBehavior`2";
    public const string IStreamPipelineBehavior2 = "SkathIO.Rogue.IStreamPipelineBehavior`2";

    // Behavior order attribute (PD-4)
    public const string BehaviorOrderAttribute   = "SkathIO.Rogue.BehaviorOrderAttribute";

    // Processors
    public const string IRequestPreProcessor1    = "SkathIO.Rogue.IRequestPreProcessor`1";
    public const string IRequestPostProcessor2   = "SkathIO.Rogue.IRequestPostProcessor`2";
    public const string IRequestExceptionHandler3 = "SkathIO.Rogue.IRequestExceptionHandler`3";
    public const string IRequestExceptionAction2  = "SkathIO.Rogue.IRequestExceptionAction`2";

    // Entry-point interfaces (for ROGUE010 nudge)
    public const string IMediator  = "SkathIO.Rogue.IMediator";
    public const string ISender    = "SkathIO.Rogue.ISender";
    public const string IPublisher = "SkathIO.Rogue.IPublisher";

    // Marker request interfaces
    public const string IRequest2    = "SkathIO.Rogue.IRequest`1";  // IRequest<TResponse>
    public const string IRequest1    = "SkathIO.Rogue.IRequest";    // IRequest (no response)
    public const string IBaseRequest = "SkathIO.Rogue.IBaseRequest";

    // Notification marker
    public const string INotification = "SkathIO.Rogue.INotification";
}
