using System;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.Consumer.Jobs.Tests;

/// <summary>The jobs scenarios on the in-memory store that SkathIO.Rogue.Jobs ships.</summary>
public sealed class InMemoryJobsTests : JobsScenarioTests
{
    protected override void AddStore(IServiceCollection services) =>
        services.AddSingleton<IJobStore>(static sp => new InMemoryJobStore(sp.GetRequiredService<TimeProvider>()));
}
