using RabbitMQ.Client;

namespace SkathIO.Rogue.Messaging.RabbitMQ;

/// <summary>The transport's record of each delivery it hands the consumer.</summary>
public sealed partial class RabbitMqTransport
{
    /// <summary>Where a delivery came from (channel, tag, channel generation), kept apart from the envelope.</summary>
    private readonly record struct DeliveryHandle(IChannel Channel, ulong Tag, int Generation);

    private sealed class Delivery : ReceivedMessage
    {
        public int Settled;

        public Delivery(MessageEnvelope envelope, int deliveryCount, RabbitMqTransport owner, DeliveryHandle handle)
            : base(envelope, deliveryCount)
        {
            Owner = owner;
            Handle = handle;
        }

        public RabbitMqTransport Owner { get; }

        public DeliveryHandle Handle { get; }

        public IChannel Channel => Handle.Channel;

        public ulong Tag => Handle.Tag;

        public int Generation => Handle.Generation;
    }
}
