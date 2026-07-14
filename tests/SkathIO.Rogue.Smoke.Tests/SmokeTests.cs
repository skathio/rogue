using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using SkathIO.Rogue.Smoke.Application.Orders;
using Xunit;

namespace SkathIO.Rogue.Smoke.Tests;

/// <summary>
/// End-to-end smoke test for SkathIO.Rogue: boots the layered SkathIO.Rogue.Smoke.Api /
/// .Application / .Infrastructure solution (mirroring GitHub issue #21's multi-project shape — see
/// that project's csproj comments) via <see cref="WebApplicationFactory{TEntryPoint}"/> and drives
/// every core dispatch kind through real HTTP calls: commands with and without a response, queries,
/// notification fan-out, streaming, a custom pipeline behavior, and FluentValidation validation
/// (success and failure paths).
/// </summary>
public sealed class SmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    // Matches the Web defaults (camelCase) minimal APIs serialize responses with — HttpClient's
    // ReadFromJsonAsync/PostAsJsonAsync use case-sensitive General defaults unless told otherwise.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public SmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task CreateOrder_WithValidPayload_PersistsAndReturns201()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/orders", new { ProductId = "widget-1", Quantity = 3 }, JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<OrderId>(JsonOptions);
        Assert.NotEqual(Guid.Empty, created!.Value);

        var getResponse = await client.GetAsync($"/orders/{created.Value}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var order = await getResponse.Content.ReadFromJsonAsync<OrderDto>(JsonOptions);
        Assert.Equal("widget-1", order!.ProductId);
        Assert.Equal(3, order.Quantity);
        Assert.False(order.Shipped);
    }

    [Fact]
    public async Task CreateOrder_WithInvalidPayload_Returns400_ValidationBehaviorShortCircuits()
    {
        var client = _factory.CreateClient();

        // Quantity <= 0 fails CreateOrderCommandValidator via the auto-woven ValidationBehavior<,>
        // (SkathIO.Rogue.Smoke.Application references SkathIO.Rogue.Validation.FluentValidation —
        // no explicit AddOpenBehavior anywhere in this host).
        var response = await client.PostAsJsonAsync("/orders", new { ProductId = "widget-2", Quantity = 0 }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetOrder_Unknown_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ShipOrder_MarksShipped_VoidCommandDispatches()
    {
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/orders", new { ProductId = "widget-3", Quantity = 1 }, JsonOptions);
        var created = await createResponse.Content.ReadFromJsonAsync<OrderId>(JsonOptions);

        var shipResponse = await client.PostAsync($"/orders/{created!.Value}/ship", content: null);
        Assert.Equal(HttpStatusCode.NoContent, shipResponse.StatusCode);

        var getResponse = await client.GetAsync($"/orders/{created.Value}");
        var order = await getResponse.Content.ReadFromJsonAsync<OrderDto>(JsonOptions);
        Assert.True(order!.Shipped);
    }

    [Fact]
    public async Task CreateOrder_PublishesEvent_BothFanOutHandlersFire()
    {
        var client = _factory.CreateClient();
        await client.PostAsync("/_diagnostics/activity/reset", content: null);

        await client.PostAsJsonAsync("/orders", new { ProductId = "widget-4", Quantity = 2 }, JsonOptions);

        var activityResponse = await client.GetAsync("/_diagnostics/activity");
        var activity = await activityResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        Assert.Contains(activity!, e => e.Contains("SendOrderConfirmationHandler", StringComparison.Ordinal));
        Assert.Contains(activity!, e => e.Contains("UpdateInventoryHandler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOrder_CustomBehaviorWrapsRequest_EnteringAndCompletingRecorded()
    {
        var client = _factory.CreateClient();
        await client.PostAsync("/_diagnostics/activity/reset", content: null);

        await client.PostAsJsonAsync("/orders", new { ProductId = "widget-5", Quantity = 1 }, JsonOptions);

        var activityResponse = await client.GetAsync("/_diagnostics/activity");
        var activity = await activityResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        Assert.Contains(activity!, e => e.Contains("OrderAuditBehavior: entering CreateOrderCommand", StringComparison.Ordinal));
        Assert.Contains(activity!, e => e.Contains("OrderAuditBehavior: completed CreateOrderCommand", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateOrder_FailedValidation_CustomBehaviorRecordsFailure_HandlerNeverRuns()
    {
        var client = _factory.CreateClient();
        await client.PostAsync("/_diagnostics/activity/reset", content: null);

        var response = await client.PostAsJsonAsync("/orders", new { ProductId = "", Quantity = 1 }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var activityResponse = await client.GetAsync("/_diagnostics/activity");
        var activity = await activityResponse.Content.ReadFromJsonAsync<string[]>(JsonOptions);
        Assert.Contains(activity!, e => e.Contains("OrderAuditBehavior: entering CreateOrderCommand", StringComparison.Ordinal));
        Assert.Contains(activity!, e => e.Contains("OrderAuditBehavior: failed CreateOrderCommand", StringComparison.Ordinal));
        Assert.DoesNotContain(activity!, e => e.Contains("CreateOrderCommandHandler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamOrders_YieldsCreatedOrder()
    {
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/orders", new { ProductId = "widget-6", Quantity = 5 }, JsonOptions);
        var created = await createResponse.Content.ReadFromJsonAsync<OrderId>(JsonOptions);

        var streamResponse = await client.GetAsync("/orders/stream");
        var stream = await streamResponse.Content.ReadFromJsonAsync<OrderDto[]>(JsonOptions);

        Assert.Contains(stream!, o => o.Id == created!.Value && o.ProductId == "widget-6");
    }

    [Fact]
    public async Task MultiProjectHost_DispatchesAcrossAllThreeLayers_NoUnregisteredRequestException()
    {
        // GitHub issue #21 regression guard at the HTTP-boundary level: SkathIO.Rogue.Smoke.Api
        // declares zero handlers/behaviors of its own and reaches SkathIO.Rogue.Validation
        // .FluentValidation only transitively (through SkathIO.Rogue.Smoke.Application). Every
        // request below crosses that project boundary; before the PD-17/PD-45 fix this shape could
        // non-deterministically surface RogueUnregisteredRequestException as HTTP 500, depending on
        // module-initializer load order. This test doesn't force a specific order (that's the
        // deterministic generator-level regression test's job —
        // MultiProjectBehaviorSuppressionTests in SkathIO.Rogue.Generator.Tests); it pins that the
        // whole layered solution dispatches correctly end-to-end, every time it runs.
        var client = _factory.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/orders", new { ProductId = "cross-layer", Quantity = 1 }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<OrderId>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/orders/{created!.Value}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/orders/{created.Value}/ship", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/orders/stream")).StatusCode);
    }
}
