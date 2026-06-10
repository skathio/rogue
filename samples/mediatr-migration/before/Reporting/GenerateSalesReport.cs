using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Reporting;

public sealed record SalesReport(string Period, decimal Total);

public sealed record GenerateSalesReportQuery(string Period) : IRequest<SalesReport>;

public sealed class GenerateSalesReportQueryHandler : IRequestHandler<GenerateSalesReportQuery, SalesReport>
{
    public async Task<SalesReport> Handle(GenerateSalesReportQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new SalesReport(request.Period, 1000m);
    }
}
