using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>Second of two fan-out handlers for <see cref="ItemCreatedNotification"/> (FR-4, FR-10).</summary>
public sealed class ItemCreatedHandler2 : IEventHandler<ItemCreatedNotification>
{
    private readonly ILogger<ItemCreatedHandler2> _logger;
    private readonly IHandlerCallTracker _tracker;

    /// <summary>Initializes the handler.</summary>
    public ItemCreatedHandler2(ILogger<ItemCreatedHandler2> logger, IHandlerCallTracker tracker)
    {
        _logger = logger;
        _tracker = tracker;
    }

    /// <inheritdoc />
    public ValueTask Handle(ItemCreatedNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation("ItemCreatedHandler2 handled item {ItemId}", notification.Id);
        _tracker.Record(nameof(ItemCreatedHandler2));
        return default;
    }
}
