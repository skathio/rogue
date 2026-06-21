using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>First of two fan-out handlers for <see cref="ItemCreatedNotification"/> (FR-4, FR-10).</summary>
public sealed class ItemCreatedHandler1 : IEventHandler<ItemCreatedNotification>
{
    private readonly ILogger<ItemCreatedHandler1> _logger;
    private readonly IHandlerCallTracker _tracker;

    /// <summary>Initializes the handler.</summary>
    public ItemCreatedHandler1(ILogger<ItemCreatedHandler1> logger, IHandlerCallTracker tracker)
    {
        _logger = logger;
        _tracker = tracker;
    }

    /// <inheritdoc />
    public ValueTask Handle(ItemCreatedNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation("ItemCreatedHandler1 handled item {ItemId}", notification.Id);
        _tracker.Record(nameof(ItemCreatedHandler1));
        return default;
    }
}
