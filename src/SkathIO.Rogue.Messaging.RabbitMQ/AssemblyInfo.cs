using System.Runtime.CompilerServices;

// The wire mapping (names, delivery counts, headers) is internal; its tests read deliveries built in
// memory, so they run without a broker.
[assembly: InternalsVisibleTo("SkathIO.Rogue.Messaging.RabbitMQ.Tests")]
