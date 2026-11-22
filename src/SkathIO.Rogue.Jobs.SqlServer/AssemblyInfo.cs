using System.Runtime.CompilerServices;

// The poller, pruner and SQL are internal; the SQL Server tests drive them directly.
[assembly: InternalsVisibleTo("SkathIO.Rogue.Jobs.SqlServer.Tests")]
