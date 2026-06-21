using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Reporting;

// Additional Reporting handlers with minimal DTOs (all constructible with `new`).

public sealed record Revenue(string Period, decimal Amount);

public sealed record ProductRank(string Sku, int Rank);

public sealed record Dashboard(int Orders, decimal Revenue);

public sealed record GetRevenueByPeriodQuery(string Period) : IRequest<Revenue>;

public sealed class GetRevenueByPeriodQueryHandler : IRequestHandler<GetRevenueByPeriodQuery, Revenue>
{
    public async Task<Revenue> Handle(GetRevenueByPeriodQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new Revenue(request.Period, 5000m);
    }
}

public sealed record GetTopProductsQuery(int Count) : IRequest<IReadOnlyList<ProductRank>>;

public sealed class GetTopProductsQueryHandler
    : IRequestHandler<GetTopProductsQuery, IReadOnlyList<ProductRank>>
{
    public async Task<IReadOnlyList<ProductRank>> Handle(
        GetTopProductsQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new[] { new ProductRank("SKU-TOP", 1) };
    }
}

public sealed record ScheduleReportCommand(string Cron) : IRequest<Unit>;

public sealed class ScheduleReportCommandHandler : IRequestHandler<ScheduleReportCommand, Unit>
{
    public async Task<Unit> Handle(ScheduleReportCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return Unit.Value;
    }
}

public sealed record GetDashboardDataQuery(string Tenant) : IRequest<Dashboard>;

public sealed class GetDashboardDataQueryHandler : IRequestHandler<GetDashboardDataQuery, Dashboard>
{
    public async Task<Dashboard> Handle(GetDashboardDataQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new Dashboard(Orders: 10, Revenue: 1234m);
    }
}

public sealed record ExportAuditLogCommand(string Format) : IRequest<bool>;

public sealed class ExportAuditLogCommandHandler : IRequestHandler<ExportAuditLogCommand, bool>
{
    public async Task<bool> Handle(ExportAuditLogCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return request.Format.Length > 0;
    }
}
