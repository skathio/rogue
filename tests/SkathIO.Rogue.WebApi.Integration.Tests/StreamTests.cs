using System;
using System.Collections.Generic;
using System.Linq;
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
/// HTTP-boundary coverage for streaming dispatch (<c>CreateStream</c>). Boots the 7.2.1 host once per
/// class via <see cref="IClassFixture{T}"/>.
/// </summary>
public sealed class StreamTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StreamTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // Covers: FR-5 — IStreamQuery<T> dispatch via ISender.CreateStream.
    // Covers: FR-11 — IStreamQueryHandler<,> yields IAsyncEnumerable<T>; elements stream to the
    // response (the host streams 10 integers 0..9).
    [Fact]
    public async Task Stream_YieldsAllElements()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<int>>();
        Assert.NotNull(items);
        Assert.Equal(Enumerable.Range(0, 10).ToList(), items);
    }

    // Covers: FR-14 (CreateStream path) — ISender.CreateStream is CancellationToken-aware.
    // A pre-cancelled token surfaces OperationCanceledException when the stream is enumerated.
    [Fact]
    public async Task CreateStream_CancelledToken_SurfacesCancellation()
    {
        using var scope = _factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (int _ in sender.CreateStream(new NumberStreamRequest(1000), cts.Token))
            { }
        });
    }
}
