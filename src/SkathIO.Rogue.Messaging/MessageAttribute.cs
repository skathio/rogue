using System;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// Marks a type as a message and gives it the stable name it travels under. A message is looked up by
/// this name on the way in, never by its type name, so the type can be renamed or moved without
/// breaking senders, and a service written in another language can send it. Mark an
/// <see cref="IEvent"/> to have it published to its handlers through <see cref="IPublisher"/> when it
/// arrives, and also sent to the bus whenever it is published through <see cref="IPublisher"/>; mark an
/// <see cref="ICommand"/> or <see cref="ICommand{TResponse}"/> to have it sent to its handler through
/// <see cref="ISender"/>. Any other marked type can be sent with <see cref="IMessageBus"/> but not
/// handled here.
/// </summary>
/// <remarks>
/// A name is 1 to <see cref="MessageLimits.MaxNameLength"/> characters: ASCII letters and digits, and
/// <c>.</c>, <c>_</c>, <c>-</c> and <c>:</c>. An invalid name, or one taken by two types, fails the host
/// at startup.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class MessageAttribute : Attribute
{
    /// <summary>Initializes the attribute with the message's stable name.</summary>
    /// <param name="name">The name the message travels under, such as <c>orders.created</c>.</param>
    public MessageAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The name the message travels under.</summary>
    public string Name { get; }
}
