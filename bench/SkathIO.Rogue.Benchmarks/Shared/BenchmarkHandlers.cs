using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Benchmarks;

// Shared request/handler types used across the benchmark classes. The two benchmarked
// libraries use different marker interfaces, so each gets a parallel set of types with
// byte-identical no-op bodies. The suite measures Rogue vs MediatR.

// --- Rogue handlers ---

/// <summary>Rogue request echoing its payload (plain record, no heap-allocating fields).</summary>
public sealed record PingRequest(string Payload) : ICommand<string>;

/// <summary>Rogue handler — no-op dispatch-only body (no await, no async).</summary>
public sealed class PingHandler : ICommandHandler<PingRequest, string>
{
    public ValueTask<string> Handle(PingRequest request, CancellationToken ct)
        => new(request.Payload);
}

// --- MediatR handlers ---

/// <summary>MediatR request mirroring <see cref="PingRequest"/>.</summary>
public sealed record MediatRPingRequest(string Payload) : global::MediatR.IRequest<string>;

/// <summary>MediatR handler — byte-identical no-op body to <see cref="PingHandler"/>.</summary>
public sealed class MediatRPingHandler : global::MediatR.IRequestHandler<MediatRPingRequest, string>
{
    public Task<string> Handle(MediatRPingRequest request, CancellationToken ct)
        => Task.FromResult(request.Payload);
}

// ─────────────────────────────────────────────────────────────────────────────────────
// Notification / fan-out types (scenario 4 + scenario 6) — parameterized fan-out.
//
// Fan-out count N is controlled by declaring a *distinct notification type per N*, each with
// exactly N distinct handler classes. This is the only design that yields an identical N for
// both libraries: MediatR's scanner auto-discovers EVERY handler for a given notification type
// (so duplicate-registration / selective-registration tricks do not control N for it). One
// notification type per N sidesteps that entirely — each library fans out to exactly the N
// handlers declared for that type.
// ─────────────────────────────────────────────────────────────────────────────────────

// --- Rogue notification types (fan-out resolved at runtime via GetServices<IEventHandler<T>>) ---

public sealed record PingNotificationN2(string Payload) : IEvent;
public sealed class PingNotificationN2Handler1 : IEventHandler<PingNotificationN2>
{
    public ValueTask Handle(PingNotificationN2 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN2Handler2 : IEventHandler<PingNotificationN2>
{
    public ValueTask Handle(PingNotificationN2 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}

public sealed record PingNotificationN5(string Payload) : IEvent;
public sealed class PingNotificationN5Handler1 : IEventHandler<PingNotificationN5>
{
    public ValueTask Handle(PingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN5Handler2 : IEventHandler<PingNotificationN5>
{
    public ValueTask Handle(PingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN5Handler3 : IEventHandler<PingNotificationN5>
{
    public ValueTask Handle(PingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN5Handler4 : IEventHandler<PingNotificationN5>
{
    public ValueTask Handle(PingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN5Handler5 : IEventHandler<PingNotificationN5>
{
    public ValueTask Handle(PingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}

public sealed record PingNotificationN20(string Payload) : IEvent;
public sealed class PingNotificationN20Handler1 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler2 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler3 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler4 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler5 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler6 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler7 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler8 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler9 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler10 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler11 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler12 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler13 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler14 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler15 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler16 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler17 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler18 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler19 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler20 : IEventHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}

// --- MediatR notification types (handlers discovered by RegisterServicesFromAssembly scan) ---

public sealed record MtrPingNotificationN2(string Payload) : global::MediatR.INotification;
public sealed class MtrPingNotificationN2Handler1 : global::MediatR.INotificationHandler<MtrPingNotificationN2>
{
    public Task Handle(MtrPingNotificationN2 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN2Handler2 : global::MediatR.INotificationHandler<MtrPingNotificationN2>
{
    public Task Handle(MtrPingNotificationN2 notification, CancellationToken ct)
        => Task.CompletedTask;
}

public sealed record MtrPingNotificationN5(string Payload) : global::MediatR.INotification;
public sealed class MtrPingNotificationN5Handler1 : global::MediatR.INotificationHandler<MtrPingNotificationN5>
{
    public Task Handle(MtrPingNotificationN5 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN5Handler2 : global::MediatR.INotificationHandler<MtrPingNotificationN5>
{
    public Task Handle(MtrPingNotificationN5 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN5Handler3 : global::MediatR.INotificationHandler<MtrPingNotificationN5>
{
    public Task Handle(MtrPingNotificationN5 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN5Handler4 : global::MediatR.INotificationHandler<MtrPingNotificationN5>
{
    public Task Handle(MtrPingNotificationN5 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN5Handler5 : global::MediatR.INotificationHandler<MtrPingNotificationN5>
{
    public Task Handle(MtrPingNotificationN5 notification, CancellationToken ct)
        => Task.CompletedTask;
}

public sealed record MtrPingNotificationN20(string Payload) : global::MediatR.INotification;
public sealed class MtrPingNotificationN20Handler1 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler2 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler3 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler4 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler5 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler6 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler7 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler8 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler9 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler10 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler11 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler12 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler13 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler14 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler15 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler16 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler17 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler18 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler19 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}
public sealed class MtrPingNotificationN20Handler20 : global::MediatR.INotificationHandler<MtrPingNotificationN20>
{
    public Task Handle(MtrPingNotificationN20 notification, CancellationToken ct)
        => Task.CompletedTask;
}

// ─────────────────────────────────────────────────────────────────────────────────────
// Stream types (scenario 5) — Rogue only.
// MediatR v12's CORE package has no IStreamQuery equivalent (streaming lives in
// MediatR.Extensions.Microsoft.DependencyInjection's IStreamQueryHandler, not referenced
// here), so the streaming benchmark intentionally omits MediatR — see StreamBenchmarks.cs.
// ─────────────────────────────────────────────────────────────────────────────────────

// --- Rogue stream types ---
public sealed record PingStreamRequest(int Count) : IStreamQuery<int>;
public sealed class PingStreamHandler : IStreamQueryHandler<PingStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(PingStreamRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (int i = 0; i < request.Count; i++)
        {
            yield return i;
        }
        await Task.CompletedTask; // suppress CS1998
    }
}
