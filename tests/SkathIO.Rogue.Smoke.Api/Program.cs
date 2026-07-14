using System;
using System.Threading;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using SkathIO.Rogue.Logging;
using SkathIO.Rogue.Smoke.Application;
using SkathIO.Rogue.Smoke.Application.Orders;
using SkathIO.Rogue.Smoke.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Composition root: each layer wires its own services. Note this host never references
// SkathIO.Rogue.Validation.FluentValidation directly — it only sees ValidationBehavior<,> because
// SkathIO.Rogue.Smoke.Application (referenced below) does, and that reference flows transitively.
// That is the exact multi-project shape GitHub issue #21 describes.
builder.Services.AddSmokeInfrastructure();
builder.Services.AddSmokeApplication();
builder.Services.AddRogue(o => o.AddOpenBehavior(typeof(LoggingBehavior<,>)));
builder.Services.AddLogging();

var app = builder.Build();

// Maps a failed FluentValidation behavior to 400, mirroring SkathIO.Rogue.Sample.WebApi.
app.Use(async (ctx, next) =>
{
    try
    {
        await next(ctx);
    }
    catch (ValidationException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { errors = ex.Errors });
    }
    catch (OrderNotFoundException)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }
});

// ICommand<TResponse> + auto-woven FluentValidation behavior + internal event publish.
app.MapPost("/orders", async (CreateOrderCommand command, ISender sender) =>
{
    OrderId orderId = await sender.Send(command);
    return Results.Created($"/orders/{orderId.Value}", orderId);
});

// IQuery<T>. A missing order throws OrderNotFoundException, mapped to 404 by the middleware above.
app.MapGet("/orders/{id:guid}", async (Guid id, ISender sender) =>
    Results.Ok(await sender.Send(new GetOrderQuery(id))));

// ICommand (void).
app.MapPost("/orders/{id:guid}/ship", async (Guid id, ISender sender) =>
{
    await sender.Send(new MarkOrderShippedCommand(id));
    return Results.NoContent();
});

// IStreamQuery<T>.
app.MapGet("/orders/stream", (ISender sender, CancellationToken ct) =>
    Results.Ok(sender.CreateStream(new StreamOrdersQuery(), ct)));

// Diagnostics: exposes the recorded pipeline/handler activity over HTTP so the smoke test can assert
// internal effects (behavior wrapping, event fan-out) through the same HTTP boundary as everything
// else, without reaching into DI from the test.
app.MapGet("/_diagnostics/activity", (IOrderActivityLog log) => Results.Ok(log.Entries));
app.MapPost("/_diagnostics/activity/reset", (IOrderActivityLog log) =>
{
    log.Clear();
    return Results.NoContent();
});

app.Run();

/// <summary>Public entry point required for <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program { }
