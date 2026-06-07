namespace SkathIO.Rogue.Compatibility;

// NOTE: the constraint is `INotification`, not MediatR's `notnull`. Rogue's base
// INotificationHandler<in TNotification> constrains TNotification to INotification;
// a weaker `notnull` constraint here would not satisfy the base interface (CS0314).
public interface INotificationHandler<TNotification> : global::SkathIO.Rogue.INotificationHandler<TNotification>
    where TNotification : global::SkathIO.Rogue.INotification { }
