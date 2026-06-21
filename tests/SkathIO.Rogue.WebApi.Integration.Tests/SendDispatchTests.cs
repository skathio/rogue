using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Sample.WebApi;
using Xunit;

namespace SkathIO.Rogue.WebApi.Integration.Tests;

/// <summary>
/// HTTP-boundary coverage for the request/response (<c>Send</c>) and stream-entry dispatch shapes.
/// Boots the 7.2.1 host once per class via <see cref="IClassFixture{T}"/>; no static handler state.
/// </summary>
public sealed class SendDispatchTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SendDispatchTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // Covers: FR-1 — ICommand<TResponse> round-trips (Ping returns a response at the HTTP boundary).
    // Covers: FR-7 — handler returns ValueTask<TResponse>; the response is observed at HTTP.
    [Fact]
    public async Task Ping_RequestResponse_RoundTrips()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/ping", new PingRequest("hello"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PingResponse>();
        Assert.NotNull(body);
        Assert.Equal("hello", body!.Echo);
    }

    // Covers: FR-2 — ICommand (no response) completes.
    // Covers: FR-8 — no-response handler returns ValueTask; 204 No Content observed.
    [Fact]
    public async Task SilentCommand_NoResponse_Completes()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/command", new SilentCommand("payload"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // Covers: FR-3 — IQuery<T> semantic-alias contract dispatches.
    // Covers: FR-9 — IQueryHandler<,> alias dispatches (GetItemQueryHandler).
    [Fact]
    public async Task Query_SemanticAlias_Dispatches()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/query/42");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ItemResult>();
        Assert.NotNull(body);
        Assert.Equal(42, body!.Id);
        Assert.Equal("Item-42", body.Name);
    }

    // Covers: FR-14 — ISender.Send is CancellationToken-aware; the token flows through the dispatcher
    // into the handler, which awaits Task.Delay(token) and observes the cancellation.
    [Fact]
    public async Task Send_CancelledToken_SurfacesCancellation()
    {
        // NOTE: asserted against the container boundary (the same generated dispatcher every HTTP
        // request uses), not over HTTP, so the failure proves the SERVER honored the token. A
        // client-side cancellation of GetAsync would throw TaskCanceledException even if Send ignored
        // its CancellationToken — this proves token-awareness inside Send. The handler delays 10s, so
        // a pre-cancelled token must short-circuit it well before the delay elapses.
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.Send(new DelayRequest(10_000), cts.Token).AsTask());
    }

    // Covers: FR-16 — the IMediator convenience type dispatches (Send via IMediator).
    [Fact]
    public async Task Mediator_ConvenienceType_Dispatches()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mediator/ping", new PingRequest("via-mediator"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PingResponse>();
        Assert.NotNull(body);
        Assert.Equal("via-mediator", body!.Echo);
    }

    // Covers: FR-17 — the object-typed ISender.Send(object) overload dispatches a known request.
    [Fact]
    public async Task ObjectOverload_KnownType_Dispatches()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/object/ping", new PingRequest("boxed"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PingResponse>();
        Assert.NotNull(body);
        Assert.Equal("boxed", body!.Echo);
    }

    // Covers: FR-17 — an unknown object request type yields the defined exception
    // (RogueUnregisteredRequestException), surfaced at the boundary as HTTP 500.
    [Fact]
    public async Task ObjectOverload_UnknownType_YieldsDefinedException()
    {
        // NOTE: tested at container boundary, not HTTP — no suitable HTTP endpoint exists for this FR.
        // The unknown-type contract is not reachable through a typed HTTP endpoint (model binding
        // requires a known request type), so it is asserted directly against the host container — the
        // same generated dispatcher every HTTP request uses. UnregisteredRequest has no handler in the
        // host compilation, so the object-dispatch switch falls through to the defined exception.
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<RogueUnregisteredRequestException>(
            () => sender.Send((object)new UnregisteredRequest()).AsTask());
    }

    // Covers: FR-32 — a single AddRogue(...) call wires the whole host. Asserted implicitly by every
    // test dispatching against one registration; this test pins it explicitly: all five dispatch
    // shapes resolve from the one container with no per-shape registration call.
    [Fact]
    public async Task SingleAddRogue_WiresAllShapes()
    {
        var client = _factory.CreateClient();

        var ping = await client.PostAsJsonAsync("/ping", new PingRequest("x"));
        var command = await client.PostAsJsonAsync("/command", new SilentCommand("x"));
        var query = await client.GetAsync("/query/1");
        var notify = await client.PostAsJsonAsync("/notify", new ItemCreatedNotification(1));
        var stream = await client.GetAsync("/stream");

        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, command.StatusCode);
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, notify.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
    }

    // Covers: FR-35 — configured lifetimes resolve; the scoped IHandlerCallTracker is fresh per
    // request scope. Two requests each report exactly one recorded call — state never accumulates
    // across the shared host, proving per-request scoping (default transient handlers + scoped tracker).
    [Fact]
    public async Task ScopedTracker_FreshPerRequestScope()
    {
        var client = _factory.CreateClient();

        var first = await client.GetFromJsonAsync<ScopeProbeResult>("/scope-probe");
        var second = await client.GetFromJsonAsync<ScopeProbeResult>("/scope-probe");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, first!.CallCount);
        Assert.Equal(1, second!.CallCount);
    }
}

/// <summary>
/// An unregistered request type used only by <see cref="SendDispatchTests"/> to assert the
/// object-dispatch unknown-type contract (FR-17). No handler exists for it, so the generated
/// dispatcher throws <c>RogueUnregisteredRequestException</c>.
/// </summary>
public sealed record UnregisteredRequest : SkathIO.Rogue.ICommand<string>;
