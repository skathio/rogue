using System;
using System.Threading;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using SkathIO.Rogue.Logging;
using SkathIO.Rogue.Validation.FluentValidation;
using SkathIO.Rogue.Sample.WebApi;

var builder = WebApplication.CreateBuilder(args);

// Phase 5.1 is done: the Logging + Validation behavior packages are referenced and wired here.
// The host still compiles/runs without them (soft dependency, spec §6.2); they are present so the
// FR-38/FR-39 pipeline tests in Phase 7.3.2 have a real wired pipeline.
// Phase 5.1 complete — LoggingBehavior + ValidationBehavior wired unconditionally (simplified from
// spec's conditional-wiring intent, which was for NuGet-package-absent scenarios). Both packages are
// always referenced by this host, so the package-present/absent branch never differs (Minor #4).
builder.Services.AddRogue(o =>
{
    o.AddOpenBehavior(typeof(LoggingBehavior<,>));
    o.AddOpenBehavior(typeof(ValidationBehavior<,>));
    // FR-17: the object-typed Send/Publish overloads are opt-in (RogueOptions.EnableObjectDispatch).
    // The host enables them so the WAF suite can assert the object dispatch path at the HTTP boundary.
    o.EnableObjectDispatch = true;
});
builder.Services.AddLogging();

// DI-scoped observation seam — replaces static mutable handler state (spec §6.4, Major #7).
builder.Services.AddScoped<IHandlerCallTracker, HandlerCallTracker>();

var app = builder.Build();

// FR-39: map ValidationException → 400 so the WAF suite can assert the boundary behavior.
// Must be first in the middleware pipeline so it wraps all endpoint execution.
app.Use(async (ctx, next) =>
{
    try { await next(ctx); }
    catch (ValidationException) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; }
});

// FR-1: ICommand<TResponse>
app.MapPost("/ping", async (PingRequest req, ISender sender) =>
    Results.Ok(await sender.Send(req)));

// FR-2: ICommand (no response)
app.MapPost("/command", async (SilentCommand cmd, ISender sender) =>
{
    await sender.Send(cmd);
    return Results.NoContent();
});

// FR-3: IQuery<T>
app.MapGet("/query/{id:int}", async (int id, ISender sender) =>
    Results.Ok(await sender.Send(new GetItemQuery(id))));

// FR-4: IEvent fan-out (two handlers)
app.MapPost("/notify", async (ItemCreatedNotification notification, IPublisher publisher) =>
{
    await publisher.Publish(notification);
    return Results.Accepted();
});

// FR-5: IStreamQuery<T>
app.MapGet("/stream", (ISender sender, CancellationToken ct) =>
    Results.Ok(sender.CreateStream(new NumberStreamRequest(10), ct)));

// FR-14: ISender.Send is CancellationToken-aware — the handler awaits a cancellable delay, so the
// request's CancellationToken (cancelled by the client) surfaces an OperationCanceledException.
app.MapGet("/delay/{ms:int}", async (int ms, ISender sender, CancellationToken ct) =>
    Results.Ok(await sender.Send(new DelayRequest(ms), ct)));

// FR-16: IMediator convenience type dispatches (combines ISender + IPublisher).
app.MapPost("/mediator/ping", async (PingRequest req, IMediator mediator) =>
    Results.Ok(await mediator.Send(req)));

// FR-17: the object-typed ISender.Send(object) overload. An unknown (unregistered) request type
// surfaces RogueUnregisteredRequestException, which the host maps to HTTP 500.
app.MapPost("/object/ping", async (PingRequest req, ISender sender) =>
    Results.Ok(await sender.Send((object)req)));

// FR-13: publishing a notification with zero registered handlers is a no-op — Publish completes
// without error.
app.MapPost("/notify/unhandled", async (UnhandledNotification notification, IPublisher publisher) =>
{
    await publisher.Publish(notification);
    return Results.Accepted();
});

// FR-15: IPublisher.Publish is CancellationToken-aware. The endpoint forwards the request's token;
// a client-cancelled token surfaces an OperationCanceledException before/while fanning out.
app.MapPost("/notify/cancellable", async (ItemCreatedNotification notification, IPublisher publisher, CancellationToken ct) =>
{
    await publisher.Publish(notification, ct);
    return Results.Accepted();
});

// FR-29: a notification whose handlers both throw. The error shape depends on the configured publish
// strategy (ForeachAwait = first throw; WhenAll = AggregateException). The exception propagates and
// the host maps it to HTTP 500.
app.MapPost("/notify/faulting", async (FaultingNotification notification, IPublisher publisher) =>
{
    await publisher.Publish(notification);
    return Results.Accepted();
});

// FR-25: pre/post processors run around the handler. Dispatched through the generated loop; the
// pre-processor, handler, and post-processor each record into the scoped tracker. The endpoint
// returns the handler's response (the processors do not alter it).
app.MapPost("/processed", async (ProcessedRequest req, ISender sender) =>
    Results.Ok(await sender.Send(req)));

// FR-26: the handler throws; the registered IRequestExceptionHandler supplies a fallback response,
// which the dispatch loop returns instead of propagating. The endpoint therefore returns 200 with
// the fallback body rather than mapping an exception to 500.
app.MapPost("/faulting-request", async (FaultingRequest req, ISender sender) =>
    Results.Ok(await sender.Send(req)));

// FR-35: prove the scoped IHandlerCallTracker is fresh per request scope. Each HTTP request runs in
// its own DI scope, so the tracker starts empty every time and Calls.Count is always 1 here —
// state never leaks across requests.
app.MapGet("/scope-probe", (IHandlerCallTracker tracker) =>
{
    tracker.Record("scope-probe");
    return Results.Ok(new ScopeProbeResult(tracker.Calls.Count));
});

app.Run();

/// <summary>Public entry point required for <c>WebApplicationFactory&lt;Program&gt;</c> (PD-20).</summary>
public partial class Program { }
