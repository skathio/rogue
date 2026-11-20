using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.InteropServices;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;

namespace SkathIO.Rogue.Jobs.SqlServer;

/// <summary>
/// Builds the table-valued parameters the store sends: a batch of jobs, of leases, or of ids. Each row
/// shape matches its table type in <see cref="SqlServerSql"/>. Rows stream to the server from one
/// reused record, so a large batch is not copied into a second list first.
/// </summary>
internal static class SqlServerRows
{
    private static readonly SqlMetaData[] _jobColumns =
    {
        new SqlMetaData("ord", SqlDbType.Int),
        new SqlMetaData("id", SqlDbType.UniqueIdentifier),
        new SqlMetaData("kind", SqlDbType.NVarChar, JobLimits.MaxKindLength),
        new SqlMetaData("queue", SqlDbType.NVarChar, JobLimits.MaxQueueLength),
        new SqlMetaData("payload", SqlDbType.VarBinary, SqlMetaData.Max),
        new SqlMetaData("max_attempts", SqlDbType.Int),
    };

    private static readonly SqlMetaData[] _leaseColumns =
    {
        new SqlMetaData("id", SqlDbType.UniqueIdentifier),
        new SqlMetaData("attempt", SqlDbType.Int),
    };

    private static readonly SqlMetaData[] _idColumns =
    {
        new SqlMetaData("id", SqlDbType.UniqueIdentifier),
    };

    public static SqlParameter Jobs(string name, string typeName, IReadOnlyList<NewJob> jobs)
        => Structured(name, typeName, JobRecords(jobs));

    public static SqlParameter Leases(string name, string typeName, HashSet<JobLease> leases)
        => Structured(name, typeName, LeaseRecords(leases));

    public static SqlParameter Ids(string name, string typeName, HashSet<Guid> ids)
        => Structured(name, typeName, IdRecords(ids));

    private static SqlParameter Structured(string name, string typeName, IEnumerable<SqlDataRecord> rows)
        => new SqlParameter(name, SqlDbType.Structured) { TypeName = typeName, Value = rows };

    private static IEnumerable<SqlDataRecord> JobRecords(IReadOnlyList<NewJob> jobs)
    {
        var record = new SqlDataRecord(_jobColumns);
        for (int i = 0; i < jobs.Count; i++)
        {
            NewJob job = jobs[i];
            record.SetInt32(0, i);
            record.SetGuid(1, job.Id);
            record.SetString(2, job.Kind);
            record.SetString(3, job.Queue);
            record.SetValue(4, Bytes(job.Payload));
            record.SetInt32(5, job.MaxAttempts);
            yield return record;
        }
    }

    private static IEnumerable<SqlDataRecord> LeaseRecords(HashSet<JobLease> leases)
    {
        var record = new SqlDataRecord(_leaseColumns);
        foreach (JobLease lease in leases)
        {
            record.SetGuid(0, lease.Id);
            record.SetInt32(1, lease.Attempt);
            yield return record;
        }
    }

    private static IEnumerable<SqlDataRecord> IdRecords(HashSet<Guid> ids)
    {
        var record = new SqlDataRecord(_idColumns);
        foreach (Guid id in ids)
        {
            record.SetGuid(0, id);
            yield return record;
        }
    }

    // The payload's own array when it spans one whole array, else a copy.
    internal static byte[] Bytes(ReadOnlyMemory<byte> payload)
        => MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : payload.ToArray();
}
