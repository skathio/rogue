using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RabbitMQ.Client.Events;

namespace SkathIO.Rogue.Messaging.RabbitMQ;

/// <summary>
/// Signs the delivery count of each copy the transport sends back to its own queue, and reads a
/// delivery's count only from such a signature. The count travels on the copy, in the
/// <see cref="RabbitMqWire.DeliveryStampHeader"/> header, as the count, a dot, and an HMAC-SHA256 over the
/// message's name, id and count under <see cref="RabbitMqTransportOptions.DeliveryStampKey"/>. A sender
/// has no key, so a message it publishes, whatever id it reuses or header it sets, is on its first
/// delivery; and since nothing is kept per message, no number of messages in retry can make the
/// transport lose a count.
/// </summary>
/// <remarks>
/// Every instance of a service is given the same key, so a copy one instance puts back is read with its
/// count by any other, and after a restart. A copy signed under another key starts again from 1.
/// </remarks>
internal sealed class DeliveryStamp
{
    private readonly byte[] _key;

    /// <summary>Signs and verifies under the UTF-8 bytes of <paramref name="key"/>, which the stamp never shows.</summary>
    public DeliveryStamp(string key)
    {
        _key = Encoding.UTF8.GetBytes(key);
    }

    /// <summary>The stamp of a copy of the message named <paramref name="name"/> with id <paramref name="id"/>, on delivery <paramref name="count"/>.</summary>
    public string Sign(string name, string id, int count)
        => count.ToString(CultureInfo.InvariantCulture) + "." + Convert.ToHexString(Mac(name, id, count));

    /// <summary>
    /// How many times <paramref name="delivery"/>, read as <paramref name="envelope"/>, has been delivered:
    /// the signed count of a copy this transport put back on its queue through the default exchange,
    /// held to 1 to <see cref="RabbitMqWire.MaxDeliveryCount"/>, otherwise 1; one more when the broker
    /// marks it redelivered after a delivery that was never settled.
    /// </summary>
    public int CountOf(BasicDeliverEventArgs delivery, MessageEnvelope envelope)
    {
        int count = 1;
        if (delivery.Exchange.Length == 0
            && Verified(RabbitMqWire.HeaderString(delivery.BasicProperties, RabbitMqWire.DeliveryStampHeader), envelope) is int signed)
        {
            count = Math.Clamp(signed, 1, RabbitMqWire.MaxDeliveryCount);
        }
        return count + (delivery.Redelivered ? 1 : 0);
    }

    // The count of a stamp that this transport signed for the envelope's name and id, or null.
    private int? Verified(string? stamp, MessageEnvelope envelope)
    {
        if (stamp is null)
        {
            return null;
        }
        int dot = stamp.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || !int.TryParse(stamp.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            return null;
        }
        byte[] mac;
        try
        {
            mac = Convert.FromHexString(stamp.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return null;
        }
        return CryptographicOperations.FixedTimeEquals(mac, Mac(envelope.Name, envelope.Id, count)) ? count : null;
    }

    // Each field is length-prefixed, so no two (name, id, count) give the same signed bytes.
    private byte[] Mac(string name, string id, int count)
    {
        int nameBytes = Encoding.UTF8.GetByteCount(name);
        int idBytes = Encoding.UTF8.GetByteCount(id);
        byte[] data = new byte[(3 * sizeof(int)) + nameBytes + idBytes];
        Span<byte> rest = data;
        BinaryPrimitives.WriteInt32LittleEndian(rest, nameBytes);
        rest = rest.Slice(sizeof(int));
        rest = rest.Slice(Encoding.UTF8.GetBytes(name, rest));
        BinaryPrimitives.WriteInt32LittleEndian(rest, idBytes);
        rest = rest.Slice(sizeof(int));
        rest = rest.Slice(Encoding.UTF8.GetBytes(id, rest));
        BinaryPrimitives.WriteInt32LittleEndian(rest, count);
        return HMACSHA256.HashData(_key, data);
    }
}
