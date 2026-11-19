using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Handles an event of type <typeparamref name="TEvent"/> (D5/FR-2, PD-42).</summary>
public interface IEventHandler<in TEvent>
    where TEvent : IEvent
{
    /// <summary>Handles the event.</summary>
    ValueTask Handle(TEvent @event, CancellationToken cancellationToken);
}
