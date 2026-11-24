using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Handles an event of type <typeparamref name="TEvent"/>; an event may have any number of handlers.</summary>
public interface IEventHandler<in TEvent>
    where TEvent : IEvent
{
    /// <summary>Handles the event.</summary>
    ValueTask Handle(TEvent @event, CancellationToken cancellationToken);
}
