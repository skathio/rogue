using System;

namespace SkathIO.Rogue.Logging;

/// <summary>
/// Opt-in marker permitting <see cref="LoggingBehavior{TRequest,TResponse}"/> to log the request
/// (and response) payload for the decorated request type. Absent this attribute — and absent
/// <see cref="LoggingOptions.LogPayload"/> — payloads are never logged (NFR-SEC-2). Apply only to
/// request types whose payloads are known to be free of secrets or PII.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class LogPayloadAttribute : Attribute
{
}
