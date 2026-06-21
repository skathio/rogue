using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// xUnit collection for tests that dispatch through a real DI provider built via
/// <see cref="GeneratorTestHelper.BuildProviderFromGenerated"/>. That helper mutates the
/// <em>process-global, append-only</em> <c>SkathIO.Rogue.RogueRegistrationBridge</c> registry
/// (snapshot → reset → run this assembly's module init → AddRogue → restore). xUnit serialises tests
/// within a single class but runs distinct classes in parallel by default; two classes touching the
/// bridge concurrently race on that shared registry (a stale/foreign registrar can win PD-38's
/// first-wins <c>TryAddScoped</c> and shadow the assembly under test). Placing every bridge-touching
/// class in this one collection makes them share a test collection, so xUnit runs them serially.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RealDiDispatchCollection
{
    public const string Name = "Rogue real-DI dispatch (process-global registration bridge)";
}
