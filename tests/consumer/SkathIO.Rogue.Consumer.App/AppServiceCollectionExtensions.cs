using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue.Consumer.App;

public static class AppServiceCollectionExtensions
{
    /// <summary>The app's own services. Handlers, behaviors and validators need no registration here.</summary>
    public static IServiceCollection AddConsumerApp(this IServiceCollection services)
    {
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<OrderBook>();
        return services;
    }
}
