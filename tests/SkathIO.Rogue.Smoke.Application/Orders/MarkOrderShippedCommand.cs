using System;

namespace SkathIO.Rogue.Smoke.Application.Orders;

/// <summary>A void command (<see cref="ICommand"/>, no response) — the smoke test's coverage for the
/// no-response dispatch path.</summary>
public sealed record MarkOrderShippedCommand(Guid OrderId) : ICommand;
