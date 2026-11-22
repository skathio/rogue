using System.Runtime.CompilerServices;

// The listener, pruner and completion batcher are internal; the Postgres tests drive them directly.
[assembly: InternalsVisibleTo("SkathIO.Rogue.Jobs.Postgres.Tests")]
