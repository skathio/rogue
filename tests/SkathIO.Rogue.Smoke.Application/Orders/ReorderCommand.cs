using System;
using System.Collections.Generic;
using SkathIO.Rogue.Validation;

namespace SkathIO.Rogue.Smoke.Application.Orders;

/// <summary>
/// Places new orders from an existing one. Validated by SkathIO.Rogue.Validation: rule attributes on
/// this type and on each <see cref="ReorderLine"/> (paths such as <c>Lines[1].Quantity</c>), and
/// <see cref="ReorderCommandChecks"/>, an async rule resolved from DI that needs the Infrastructure
/// layer's order store. Returns the number of orders placed.
/// </summary>
[Validate]
public sealed partial record ReorderCommand : ICommand<int>
{
    [NotEmpty]
    public Guid SourceOrderId { get; init; }

    [NotEmpty, MaximumLength(10)]
    public IReadOnlyList<ReorderLine> Lines { get; init; } = Array.Empty<ReorderLine>();
}

/// <summary>One product and quantity of a <see cref="ReorderCommand"/>.</summary>
[Validate]
public sealed partial record ReorderLine
{
    [NotEmpty]
    public string ProductId { get; init; } = string.Empty;

    [GreaterThan(0)]
    public int Quantity { get; init; }
}
