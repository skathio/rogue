using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Reporting;

public sealed record ExportDataCommand(string Format) : IRequest;

public sealed class ExportDataCommandHandler : IRequestHandler<ExportDataCommand>
{
    public async Task Handle(ExportDataCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}
