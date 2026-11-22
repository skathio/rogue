using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs.Postgres.Tests;

/// <summary>The values the test job's handler ran with.</summary>
public sealed class Ran
{
    public ConcurrentQueue<string> Values { get; } = new ConcurrentQueue<string>();
}

[Job]
public sealed record PostgresTestJob(string Value) : ICommand;

public sealed class PostgresTestJobHandler : ICommandHandler<PostgresTestJob>
{
    private readonly Ran _ran;

    public PostgresTestJobHandler(Ran ran) => _ran = ran;

    public ValueTask Handle(PostgresTestJob command, CancellationToken cancellationToken)
    {
        _ran.Values.Enqueue(command.Value);
        return default;
    }
}

[JsonSerializable(typeof(PostgresTestJob))]
public sealed partial class PostgresTestJson : JsonSerializerContext
{
}
