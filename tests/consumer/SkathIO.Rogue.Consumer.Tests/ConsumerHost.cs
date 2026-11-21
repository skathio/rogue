using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Consumer.App;

namespace SkathIO.Rogue.Consumer.Tests;

/// <summary>A service provider wired as an app wires it, with one request scope.</summary>
internal sealed class ConsumerHost : IAsyncDisposable
{
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;

    public ConsumerHost()
    {
        _root = new ServiceCollection().AddConsumerApp().AddRogue().BuildServiceProvider(validateScopes: true);
        _scope = _root.CreateAsyncScope();
    }

    public IMediator Mediator => _scope.ServiceProvider.GetRequiredService<IMediator>();

    public ActivityLog Log => _root.GetRequiredService<ActivityLog>();

    public OrderBook Orders => _root.GetRequiredService<OrderBook>();

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync().ConfigureAwait(false);
        await _root.DisposeAsync().ConfigureAwait(false);
    }
}
