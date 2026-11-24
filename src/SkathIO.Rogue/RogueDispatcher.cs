using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// The dispatcher base type. The source generator emits a sealed subclass
/// (<c>SkathIO.Rogue.Generated.RogueDispatcherImpl</c>) in the consumer's compilation that
/// overrides the virtual dispatch methods. This base provides the throwing stubs so the runtime
/// compiles before the generator has run, and so the type can be referenced across the assembly
/// boundary (a generated subclass works where a cross-assembly <c>partial</c> cannot).
/// </summary>
/// <remarks>
/// Commands and queries are independent contracts, so the dispatch entry points are split by
/// message family — there is no shared <c>IRequest</c> marker. Commands and queries each get their own virtual <c>Send</c>
/// overload (plus a void-command overload); the generated subclass overrides them with concrete
/// per-message <c>switch</c>es, and resolves each handler under the same service type it was
/// registered with. This base declares the CQS-shaped <em>signatures</em> so the runtime and the
/// generated code compile against the same core.
/// </remarks>
public class RogueDispatcher
{
    /// <summary>The DI service provider. Available to the generated subclass.</summary>
    protected readonly IServiceProvider _serviceProvider;

    /// <summary>Initializes the dispatcher.</summary>
    public RogueDispatcher(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>Sends a command that produces no return value. Overridden by the generator.</summary>
    public virtual ValueTask Send(ICommand command, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found. Ensure the SkathIO.Rogue source generator has run.");

    /// <summary>Sends a strongly-typed command. Overridden by the generator.</summary>
    public virtual ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found. Ensure the SkathIO.Rogue source generator has run.");

    /// <summary>Sends a strongly-typed query. Overridden by the generator.</summary>
    public virtual ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found. Ensure the SkathIO.Rogue source generator has run.");

    /// <summary>Sends via the object-dispatch path. Overridden by the generator.</summary>
    public virtual ValueTask<object?> SendObject(object request, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found.");

    /// <summary>Creates a stream. Overridden by the generator.</summary>
    public virtual IAsyncEnumerable<TItem> CreateStream<TItem>(IStreamQuery<TItem> query, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found.");

    /// <summary>Publishes an event. Overridden by the generator.</summary>
    public virtual ValueTask Publish(IEvent @event, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found.");
}
