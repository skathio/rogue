using System.Linq;
using SkathIO.Rogue.SourceGenerator;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

public sealed class DiscoveryTests
{
    [Fact]
    public void Handler_ImplementingIRequestHandler_IsDiscovered()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUserQuery : IRequest<string> { }

public class GetUserHandler : IRequestHandler<GetUserQuery, string>
{
    public ValueTask<string> Handle(GetUserQuery request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Handlers);
        HandlerModel handler = models.Handlers[0];
        Assert.Equal("GetUserHandler", handler.TypeFqn.Split('.').Last());
        Assert.Contains("GetUserQuery", handler.RequestFqn);
        Assert.NotNull(handler.ResponseFqn);
        Assert.Contains("string", handler.ResponseFqn);
    }

    [Fact]
    public void Arity1Handler_HasNullResponse()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class DeleteUser : IRequest { }

public class DeleteUserHandler : IRequestHandler<DeleteUser>
{
    public ValueTask Handle(DeleteUser request, CancellationToken cancellationToken)
        => default;
}
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Handlers);
        HandlerModel handler = models.Handlers[0];
        Assert.Contains("DeleteUser", handler.RequestFqn);
        Assert.Null(handler.ResponseFqn);
    }

    [Fact]
    public void PlainClass_WithNoRogueInterface_IsNotDiscovered()
    {
        const string source = @"
public class PlainService
{
    public void DoSomething() { }
}
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Empty(models.Handlers);
        Assert.Empty(models.NotificationHandlers);
        Assert.Empty(models.Behaviors);
        Assert.Empty(models.Processors);
        Assert.Empty(models.StreamHandlers);
    }

    [Fact]
    public void ZeroHandlers_DoesNotCrash()
    {
        const string source = @"
public class EmptyClass { }
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Empty(models.Handlers);
    }

    [Fact]
    public void NotificationHandler_IsDiscovered()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class OrderPlaced : INotification { }

public class OrderPlacedHandler : INotificationHandler<OrderPlaced>
{
    public ValueTask Handle(OrderPlaced notification, CancellationToken cancellationToken)
        => default;
}
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.NotificationHandlers);
        NotificationHandlerModel handler = models.NotificationHandlers[0];
        Assert.Equal("OrderPlacedHandler", handler.TypeFqn.Split('.').Last());
        Assert.Contains("OrderPlaced", handler.NotificationFqn);
    }

    [Fact]
    public void StreamHandler_IsDiscovered()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Collections.Generic;
using System.Threading;

public class Tail : IStreamRequest<string> { }

public class TailHandler : IStreamRequestHandler<Tail, string>
{
    public IAsyncEnumerable<string> Handle(Tail request, CancellationToken cancellationToken)
        => throw new System.NotImplementedException();
}
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.StreamHandlers);
        StreamHandlerModel handler = models.StreamHandlers[0];
        Assert.Equal("TailHandler", handler.TypeFqn.Split('.').Last());
        Assert.Contains("Tail", handler.RequestFqn);
        Assert.Contains("string", handler.ResponseElementFqn);
    }

    [Fact]
    public void MultipleHandlerTypes_AreAllDiscovered()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class GetUser : IRequest<string> { }
public class GetUserHandler : IRequestHandler<GetUser, string>
{
    public ValueTask<string> Handle(GetUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}

public class UserCreated : INotification { }
public class UserCreatedHandler : INotificationHandler<UserCreated>
{
    public ValueTask Handle(UserCreated notification, CancellationToken cancellationToken)
        => default;
}
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Handlers);
        Assert.Single(models.NotificationHandlers);
    }

    [Fact]
    public void ClassWithUnrelatedBaseType_IsNotDiscovered()
    {
        const string source = @"
public class Base { }
public class Derived : Base { }
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Empty(models.Handlers);
        Assert.Empty(models.NotificationHandlers);
        Assert.Empty(models.Behaviors);
    }

    [Fact]
    public void CommandHandler_IsDiscovered()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class CreateUser : ICommand<string> { }

public class CreateUserHandler : ICommandHandler<CreateUser, string>
{
    public ValueTask<string> Handle(CreateUser request, CancellationToken cancellationToken)
        => new ValueTask<string>(string.Empty);
}
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Handlers);
        Assert.Contains("CreateUser", models.Handlers[0].RequestFqn);
    }

    [Fact]
    public void PipelineBehavior_IsDiscovered()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public ValueTask<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => next();
}
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Behaviors);
        BehaviorModel behavior = models.Behaviors[0];
        Assert.True(behavior.IsOpen);
        Assert.False(behavior.IsStream);
    }

    [Fact]
    public void ClosedBehavior_IsDiscovered_WithIsOpenFalse()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class Ping : IRequest<string> { }

// Closed (non-generic) behavior — implements IPipelineBehavior for one specific request type
public class PingLoggingBehavior : IPipelineBehavior<Ping, string>
{
    public ValueTask<string> Handle(Ping request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        => next();
}
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Behaviors);
        Assert.False(models.Behaviors[0].IsOpen);
        Assert.False(models.Behaviors[0].IsStream);
    }

    [Fact]
    public void AbstractHandler_IsStillCollected()
    {
        // Abstract types are collected here; ROGUE005 in Phase 3.2 flags them as a diagnostic.
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;

public class Ping : IRequest<string> { }

public abstract class PingHandlerBase : IRequestHandler<Ping, string>
{
    public abstract ValueTask<string> Handle(Ping request, CancellationToken cancellationToken);
}
";
        DiscoveredModels models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Handlers);
        Assert.Equal("PingHandlerBase", models.Handlers[0].TypeFqn.Split('.').Last());
    }
}
