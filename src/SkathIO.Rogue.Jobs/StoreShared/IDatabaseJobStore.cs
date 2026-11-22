using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs.Stores;

/// <summary>What the shared registration needs from a database store: its options and its prune.</summary>
/// <remarks>Shared source: each database store package compiles its own internal copy.</remarks>
internal interface IDatabaseJobStore : IJobStore
{
    /// <summary>The store's own copy of its options.</summary>
    DatabaseJobsOptions StoreOptions { get; }

    /// <summary>Deletes finished jobs past their retention and returns how many it deleted.</summary>
    Task<int> PruneAsync(CancellationToken cancellationToken);
}
