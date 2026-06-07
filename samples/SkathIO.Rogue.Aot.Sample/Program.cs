// AOT sample — a minimal PublishAot=true host exercising AddRogue() end-to-end.
// Uses a raw ServiceCollection (NOT Host.CreateDefaultBuilder) to keep the dependency
// tree small and trim-clean. The CI aot-publish job publishes this with PublishAot=true
// and fails on any trim/AOT warning (AC-E / NFR-SEC-1 gate).
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;

var services = new ServiceCollection();
services.AddRogue();
using var provider = services.BuildServiceProvider();

var sender = provider.GetRequiredService<ISender>();
var result = await sender.Send(new PingRequest("hello"));

System.Console.WriteLine(result);
return result == "hello" ? 0 : 1;

namespace SkathIO.Rogue
{
    /// <summary>Sample request returning the echoed payload.</summary>
    public sealed record PingRequest(string Payload) : IRequest<string>;

    /// <summary>Echoes the request payload back to the caller.</summary>
    public sealed class PingHandler : IRequestHandler<PingRequest, string>
    {
        public ValueTask<string> Handle(PingRequest request, CancellationToken cancellationToken)
            => new(request.Payload);
    }
}
