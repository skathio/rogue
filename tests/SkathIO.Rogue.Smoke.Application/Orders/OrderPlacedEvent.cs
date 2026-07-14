using System;

namespace SkathIO.Rogue.Smoke.Application.Orders;

/// <summary>Fanned out to two independent handlers — the smoke test's coverage for notification
/// fan-out.</summary>
public sealed record OrderPlacedEvent(Guid OrderId) : IEvent;
