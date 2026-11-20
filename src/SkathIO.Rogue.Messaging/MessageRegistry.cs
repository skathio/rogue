using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// Every message type known to this process, bound to its serializer. Built once from the generated
/// bindings; it fails at startup, naming each type, when a name is taken twice or a message type is not
/// covered by the serializer context.
/// </summary>
internal sealed class MessageRegistry
{
    private readonly Dictionary<string, BoundMessage> _byName = new Dictionary<string, BoundMessage>(StringComparer.Ordinal);
    private readonly Dictionary<Type, BoundMessage> _byType = new Dictionary<Type, BoundMessage>();

    public MessageRegistry(IEnumerable<MessageBinding> bindings, MessagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;

        var uncovered = new List<string>();
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (MessageBinding binding in bindings)
        {
            if (types.TryGetValue(binding.Name, out Type? taken))
            {
                if (taken != binding.MessageType)
                {
                    throw new InvalidOperationException("The message name '" + binding.Name + "' is used by both " + taken + " and " + binding.MessageType + ". Give one a different [Message] name.");
                }
                continue;
            }
            types.Add(binding.Name, binding.MessageType);

            JsonSerializerContext context = options.SerializerContext
                ?? throw new InvalidOperationException("Message types are registered but MessagingOptions.SerializerContext is not set. Set it in AddRogueMessaging to a JsonSerializerContext with [JsonSerializable(typeof(T))] for each [Message] type.");
            BoundMessage? bound = binding.Bind(context);
            if (bound is null)
            {
                uncovered.Add(binding.MessageType.ToString());
                continue;
            }
            _byName.Add(bound.Name, bound);
            _byType.Add(binding.MessageType, bound);
        }

        if (uncovered.Count > 0)
        {
            throw new InvalidOperationException("The serializer context " + options.SerializerContext!.GetType() + " does not cover these message types: " + string.Join(", ", uncovered) + ". Add [JsonSerializable(typeof(T))] for each.");
        }
    }

    public MessagingOptions Options { get; }

    public bool TryGet(string name, [NotNullWhen(true)] out BoundMessage? message) => _byName.TryGetValue(name, out message);

    public bool TryGet(Type type, [NotNullWhen(true)] out BoundMessage? message) => _byType.TryGetValue(type, out message);
}
