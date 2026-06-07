using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;

// SkathIO.Rogue greenfield quickstart.
//
// This is the whole thing: define a request + a handler, call AddRogue(), and dispatch through
// ISender. The source generator discovers GreetHandler at compile time and wires it — no reflection,
// no assembly scanning, AOT-safe.
//
//   curl "http://localhost:5000/greet/world"  ->  {"greeting":"Hello, world!"}

var builder = WebApplication.CreateBuilder(args);

// Discovers every IRequestHandler / INotificationHandler in this compilation at build time.
builder.Services.AddRogue();

var app = builder.Build();

// Send a request through the mediator; the generated dispatcher routes it to GreetHandler.
app.MapGet("/greet/{name}", async (string name, ISender sender) =>
{
    var response = await sender.Send(new GreetRequest(name));
    return Results.Ok(response);
});

app.Run();

/// <summary>A request that asks for a greeting and expects a <see cref="GreetResponse"/> back.</summary>
public sealed record GreetRequest(string Name) : IRequest<GreetResponse>;

/// <summary>The response carrying the rendered greeting.</summary>
public sealed record GreetResponse(string Greeting);

/// <summary>Handles <see cref="GreetRequest"/>. Discovered and wired by the source generator.</summary>
public sealed class GreetHandler : IRequestHandler<GreetRequest, GreetResponse>
{
    public ValueTask<GreetResponse> Handle(GreetRequest request, CancellationToken cancellationToken)
        => new(new GreetResponse($"Hello, {request.Name}!"));
}
