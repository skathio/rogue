using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Benchmarks;

// Shared request/handler types used across the benchmark classes. The three benchmarked
// libraries use different marker interfaces (PD-27), so each library gets a parallel set of
// types with byte-identical no-op bodies. Phase 7.1 measures Rogue vs MediatR; the
// martinothamar/Mediator head-to-head set lands in Phase 7.2.

// --- Rogue handlers ---

/// <summary>Rogue request echoing its payload (plain record, no heap-allocating fields).</summary>
public sealed record PingRequest(string Payload) : IRequest<string>;

/// <summary>Rogue handler — no-op dispatch-only body (no await, no async).</summary>
public sealed class PingHandler : IRequestHandler<PingRequest, string>
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

// --- martinothamar/Mediator handlers (PD-27) ---
// martinothamar's IRequestHandler<TReq,TRes>.Handle returns ValueTask<TRes> (same shape as Rogue);
// its ISender.Send returns ValueTask<T>. The AddMediator() DI extension is emitted by
// Mediator.SourceGenerator into this compilation (wired as an Analyzer in the .csproj).

/// <summary>martinothamar request mirroring <see cref="PingRequest"/>.</summary>
public sealed record MedPingRequest(string Payload) : global::Mediator.IRequest<string>;

/// <summary>martinothamar handler — byte-identical no-op body to <see cref="PingHandler"/>.</summary>
public sealed class MedPingHandler : global::Mediator.IRequestHandler<MedPingRequest, string>
{
    public ValueTask<string> Handle(MedPingRequest request, CancellationToken ct)
        => new(request.Payload);
}

// ─────────────────────────────────────────────────────────────────────────────────────
// Notification / fan-out types (scenario 4 + scenario 6) — PD-27 parameterized fan-out.
//
// Fan-out count N is controlled by declaring a *distinct notification type per N*, each with
// exactly N distinct handler classes. This is the only design that yields an identical N for
// all three libraries: martinothamar's and MediatR's generators/scanners auto-discover EVERY
// handler for a given notification type (so duplicate-registration / selective-registration
// tricks do not control N for them). One notification type per N sidesteps that entirely —
// each library fans out to exactly the N handlers declared for that type.
// ─────────────────────────────────────────────────────────────────────────────────────

// --- Rogue notification types (fan-out resolved at runtime via GetServices<INotificationHandler<T>>) ---

public sealed record PingNotificationN2(string Payload) : INotification;
public sealed class PingNotificationN2Handler1 : INotificationHandler<PingNotificationN2>
{
    public ValueTask Handle(PingNotificationN2 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN2Handler2 : INotificationHandler<PingNotificationN2>
{
    public ValueTask Handle(PingNotificationN2 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}

public sealed record PingNotificationN5(string Payload) : INotification;
public sealed class PingNotificationN5Handler1 : INotificationHandler<PingNotificationN5>
{
    public ValueTask Handle(PingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN5Handler2 : INotificationHandler<PingNotificationN5>
{
    public ValueTask Handle(PingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN5Handler3 : INotificationHandler<PingNotificationN5>
{
    public ValueTask Handle(PingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN5Handler4 : INotificationHandler<PingNotificationN5>
{
    public ValueTask Handle(PingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN5Handler5 : INotificationHandler<PingNotificationN5>
{
    public ValueTask Handle(PingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}

public sealed record PingNotificationN20(string Payload) : INotification;
public sealed class PingNotificationN20Handler1 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler2 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler3 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler4 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler5 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler6 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler7 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler8 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler9 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler10 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler11 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler12 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler13 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler14 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler15 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler16 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler17 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler18 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler19 : INotificationHandler<PingNotificationN20>
{
    public ValueTask Handle(PingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class PingNotificationN20Handler20 : INotificationHandler<PingNotificationN20>
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

// --- martinothamar/Mediator notification types ---
// martinothamar's INotificationHandler<T>.Handle returns ValueTask (same shape as Rogue);
// Mediator.SourceGenerator bakes the fan-out handler list per notification type at compile time.

public sealed record MedPingNotificationN2(string Payload) : global::Mediator.INotification;
public sealed class MedPingNotificationN2Handler1 : global::Mediator.INotificationHandler<MedPingNotificationN2>
{
    public ValueTask Handle(MedPingNotificationN2 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN2Handler2 : global::Mediator.INotificationHandler<MedPingNotificationN2>
{
    public ValueTask Handle(MedPingNotificationN2 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}

public sealed record MedPingNotificationN5(string Payload) : global::Mediator.INotification;
public sealed class MedPingNotificationN5Handler1 : global::Mediator.INotificationHandler<MedPingNotificationN5>
{
    public ValueTask Handle(MedPingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN5Handler2 : global::Mediator.INotificationHandler<MedPingNotificationN5>
{
    public ValueTask Handle(MedPingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN5Handler3 : global::Mediator.INotificationHandler<MedPingNotificationN5>
{
    public ValueTask Handle(MedPingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN5Handler4 : global::Mediator.INotificationHandler<MedPingNotificationN5>
{
    public ValueTask Handle(MedPingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN5Handler5 : global::Mediator.INotificationHandler<MedPingNotificationN5>
{
    public ValueTask Handle(MedPingNotificationN5 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}

public sealed record MedPingNotificationN20(string Payload) : global::Mediator.INotification;
public sealed class MedPingNotificationN20Handler1 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler2 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler3 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler4 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler5 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler6 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler7 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler8 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler9 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler10 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler11 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler12 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler13 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler14 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler15 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler16 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler17 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler18 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler19 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}
public sealed class MedPingNotificationN20Handler20 : global::Mediator.INotificationHandler<MedPingNotificationN20>
{
    public ValueTask Handle(MedPingNotificationN20 notification, CancellationToken ct)
        => ValueTask.CompletedTask;
}

// ─────────────────────────────────────────────────────────────────────────────────────
// Stream types (scenario 5) — Rogue + martinothamar.
// MediatR v12's CORE package has no IStreamRequest equivalent (streaming lives in
// MediatR.Extensions.Microsoft.DependencyInjection's IStreamRequestHandler, not referenced
// here), so the streaming benchmark intentionally omits MediatR — see StreamBenchmarks.cs.
// ─────────────────────────────────────────────────────────────────────────────────────

// --- Rogue stream types ---
public sealed record PingStreamRequest(int Count) : IStreamRequest<int>;
public sealed class PingStreamHandler : IStreamRequestHandler<PingStreamRequest, int>
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

// --- martinothamar/Mediator stream types ---
// Mediator.Abstractions 3.0.2 exposes IStreamRequest<T> + IStreamRequestHandler<TRequest,TResponse>
// (Handle returns IAsyncEnumerable<TResponse>) and ISender.CreateStream(IStreamRequest<T>, ct).
public sealed record MedPingStreamRequest(int Count) : global::Mediator.IStreamRequest<int>;
public sealed class MedPingStreamHandler : global::Mediator.IStreamRequestHandler<MedPingStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(MedPingStreamRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (int i = 0; i < request.Count; i++)
        {
            yield return i;
        }
        await Task.CompletedTask; // suppress CS1998
    }
}
