using System;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Messaging;

/// <summary>Sends messages to other services through the registered <see cref="IMessageTransport"/>.</summary>
public interface IMessageBus
{
    /// <summary>
    /// Serializes <paramref name="message"/> and sends it under the stable name its
    /// <see cref="MessageAttribute"/> gives, in an envelope with a new id, the correlation and causation
    /// ids of the message being handled (if any) and the current W3C trace context. Local handlers do
    /// not run; publish an event through <see cref="IPublisher"/> to run them and send it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The message's type is not marked <see cref="MessageAttribute"/> in a project that references this
    /// package, or its body is larger than <see cref="MessagingOptions.MaxMessageBytes"/>. Nothing is sent.
    /// </exception>
    ValueTask Publish<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull;
}
