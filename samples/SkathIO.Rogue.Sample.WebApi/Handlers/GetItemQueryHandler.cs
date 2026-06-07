using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>Handles <see cref="GetItemQuery"/> (FR-3, FR-9). Uses the <c>IQueryHandler</c> alias.</summary>
public sealed class GetItemQueryHandler : IQueryHandler<GetItemQuery, ItemResult>
{
    /// <inheritdoc />
    public ValueTask<ItemResult> Handle(GetItemQuery query, CancellationToken cancellationToken)
        => new(new ItemResult(query.Id, $"Item-{query.Id}"));
}
