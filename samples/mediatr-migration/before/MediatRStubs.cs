// Minimal in-compilation stand-ins for the MediatR interfaces the migration analyzer keys off.
// The before-sample is compiled in-memory by the AC-F migration gate (Migration.Tests) using only
// mscorlib + System.Threading.Tasks references — there is no real MediatR package on that path.
// This file deliberately has no `using MediatR;`, so ROGM001 leaves it untouched; on recompile
// against the real SkathIO.Rogue assemblies these types remain valid but unreferenced.
namespace MediatR
{
    public interface IRequest<TResponse> { }

    public interface IRequest { }

    public interface INotification { }

    public interface IRequestHandler<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        System.Threading.Tasks.Task<TResponse> Handle(TRequest request, System.Threading.CancellationToken cancellationToken);
    }

    public interface IRequestHandler<TRequest>
        where TRequest : IRequest
    {
        System.Threading.Tasks.Task Handle(TRequest request, System.Threading.CancellationToken cancellationToken);
    }

    public interface INotificationHandler<TNotification>
        where TNotification : INotification
    {
        System.Threading.Tasks.Task Handle(TNotification notification, System.Threading.CancellationToken cancellationToken);
    }

    public struct Unit
    {
        public static Unit Value => default;
    }
}
