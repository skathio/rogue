using System;

namespace SkathIO.Rogue;

/// <summary>
/// Thrown when an object-dispatch call cannot find a registered handler for the request type.
/// </summary>
public sealed class RogueUnregisteredRequestException : Exception
{
    /// <summary>The request type that had no registered handler.</summary>
    public Type RequestType { get; }

    /// <summary>Initializes a new instance of <see cref="RogueUnregisteredRequestException"/>.</summary>
    public RogueUnregisteredRequestException(Type requestType)
        : base($"No handler registered for request type '{requestType.FullName}'. Ensure the type implements a Rogue request interface and the generator has run.")
    {
        RequestType = requestType;
    }
}
