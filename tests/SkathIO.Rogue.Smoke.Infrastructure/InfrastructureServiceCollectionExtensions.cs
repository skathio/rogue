using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue.Smoke.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Registers this layer's services. Singleton: both stores are process-lifetime
    /// in-memory stand-ins for a real database/external system.</summary>
    public static IServiceCollection AddSmokeInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IOrderStore, InMemoryOrderStore>();
        services.AddSingleton<IOrderActivityLog, InMemoryOrderActivityLog>();
        return services;
    }
}
