namespace SkathIO.Rogue.Logging;

/// <summary>
/// Configuration for <see cref="LoggingBehavior{TRequest,TResponse}"/>. Resolved from DI; register
/// a singleton instance to override the defaults. The default instance keeps payload logging off
/// (NFR-SEC-2).
/// </summary>
public sealed class LoggingOptions
{
    /// <summary>
    /// When <c>true</c>, the behavior logs request/response payloads for every request regardless
    /// of the <see cref="LogPayloadAttribute"/>. Default: <c>false</c>. This is a global opt-in and
    /// a security-sensitive switch — enabling it logs every payload, including any secrets or PII.
    /// Prefer the per-type <see cref="LogPayloadAttribute"/> unless you have audited all requests.
    /// </summary>
    public bool LogPayload { get; set; }
}
