namespace SkathIO.Rogue.Compatibility;

// MediatR-shaped notification handler. PD-48: declared as a thin "is-a" sub-interface of the core
// IEventHandler<TNotification>. The core Handle signature already matches MediatR's (ValueTask on
// net8+, ValueTask<Unit> on netstandard2.0), so no Handle is redeclared here — an implementer
// satisfies the inherited IEventHandler<>.Handle. Because the type IS-A core IEventHandler<>, the
// generator's existing IEventHandler-keyed discovery loop registers it under IEventHandler<T> and
// Publish fans out to it with no adapter-specific code.
public interface INotificationHandler<in TNotification>
    : global::SkathIO.Rogue.IEventHandler<TNotification>
    where TNotification : INotification
{
}
