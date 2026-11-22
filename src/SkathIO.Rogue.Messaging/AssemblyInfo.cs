using System.Runtime.CompilerServices;

// The consumer's single-message step and the registry are internal; the messaging tests drive them
// directly so retries and the transport fallbacks are tested without real time.
[assembly: InternalsVisibleTo("SkathIO.Rogue.Messaging.Tests")]
