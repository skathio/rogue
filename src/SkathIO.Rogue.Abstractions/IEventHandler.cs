using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Handles an event of type <typeparamref name="TEvent"/> (D5/FR-2, PD-42).</summary>
public interface IEventHandler<in TEvent>
    where TEvent : IEvent
{
    /// <summary>Handles the event.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Handle(TEvent @event, CancellationToken cancellationToken);
#else
    ValueTask Handle(TEvent @event, CancellationToken cancellationToken);
#endif
}
