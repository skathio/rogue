using System;
#if !NETSTANDARD2_0
using System.Collections.Generic;
#endif
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
public class RogueDispatcher
{
    /// <summary>The DI service provider. Available to the generated subclass.</summary>
    protected readonly IServiceProvider _serviceProvider;

    /// <summary>Initializes the dispatcher.</summary>
    public RogueDispatcher(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>Sends a strongly-typed request. Overridden by the generator.</summary>
    public virtual ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found. Ensure the SkathIO.Rogue source generator has run.");

    /// <summary>Sends via the object-dispatch path. Overridden by the generator.</summary>
    public virtual ValueTask<object?> SendObject(object request, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found.");

#if !NETSTANDARD2_0
    /// <summary>Creates a stream. Overridden by the generator.</summary>
    public virtual IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found.");
#endif

    /// <summary>Publishes a notification. Overridden by the generator.</summary>
#if NETSTANDARD2_0
    public virtual ValueTask<Unit> Publish(INotification notification, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found.");
#else
    public virtual ValueTask Publish(INotification notification, CancellationToken cancellationToken)
        => throw new NotImplementedException("No generated dispatcher found.");
#endif
}
