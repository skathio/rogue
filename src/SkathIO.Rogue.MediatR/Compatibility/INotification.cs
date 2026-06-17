namespace SkathIO.Rogue.Compatibility;

// MediatR-shaped notification marker. PD-48: declared as a thin "is-a" sub-interface of the core
// IEvent (the F8 INotification -> IEvent mapping is unconditional — there is no [MapAsQuery]-style
// fork for events). A type implementing only this adapter marker therefore IS a core IEvent:
// Mediator.Publish(object)'s `is IEvent` guard accepts it with no core change, and the generator's
// existing IEventHandler-keyed discovery finds its handlers transitively.
public interface INotification : global::SkathIO.Rogue.IEvent { }
