using System.Text;

namespace SkathIO.Rogue.Jobs.SqlServer;

/// <summary>
/// The SQL the store runs, built once for its schema. The schema is the only text spliced in, and
/// <see cref="SqlServerJobsOptions"/> accepts only names that need no escaping; every value is a
/// parameter. Job states are stored as the <see cref="JobState"/> numbers, written as literals so the
/// filtered indexes, whose filters name those literals, serve the queries.
/// </summary>
internal sealed class SqlServerSql
{
    private const string Enqueued = "0";
    private const string Processing = "1";
    private const string Succeeded = "2";
    private const string DeadLettered = "3";

    // A write for a claimed job applies only while that claim still holds it.
    private const string Held = "state = " + Processing + " AND attempt = @attempt AND lease_until > @now AND id = @id";

    // A write over a list of leases applies to each job only while that lease's claim still holds it.
    private const string HeldLeases = "j.state = " + Processing + " AND j.lease_until > @now";

    private readonly string _schema;
    private readonly string _jobs;
    private readonly string _next;

    public SqlServerSql(string schema)
    {
        _schema = "[" + schema + "]";
        _jobs = _schema + ".[jobs]";
        _next = "NEXT VALUE FOR " + _schema + ".[job_seq]";
        Schema = schema;
        JobRows = _schema + ".[job_batch]";
        LeaseRows = _schema + ".[lease_rows]";
        IdRows = _schema + ".[id_rows]";
        MigrationPrologue = Prologue();
        Migrations = new[] { Migration(1, CreateJobsTable()) };

        // One statement for the whole batch: a duplicate id fails it all. The sequence numbers follow
        // the batch order, so jobs enqueued together are claimed in that order.
        Insert =
            "INSERT INTO " + _jobs + " (id, kind, queue, payload, max_attempts, seq, run_at)\n"
            + "SELECT id, kind, queue, payload, max_attempts, " + _next + " OVER (ORDER BY ord), @now FROM @jobs;";
        Claim = Rescue() + "\n" + TakeDue();
        Complete = UpdateLeases("state = " + Succeeded + ", lease_until = NULL, finished_at = @now");
        Extend = UpdateLeases("lease_until = @lease_until");
        Retry = UpdateHeld("state = " + Enqueued + ", run_at = @run_at, lease_until = NULL, seq = " + _next + ", last_error = @error");
        DeadLetter = UpdateHeld("state = " + DeadLettered + ", lease_until = NULL, finished_at = @now, last_error = @error");
        Release = UpdateHeld("state = " + Enqueued + ", attempt = attempt - 1, run_at = @run_at, lease_until = NULL, seq = " + _next);
        Requeue = RequeueSql();
        Get = "SELECT kind, queue, state, attempt, max_attempts, run_at, last_error FROM " + _jobs + " WHERE id = @id;";
        Prune =
            "DELETE TOP (@batch) FROM " + _jobs + "\n"
            + "WHERE (state = " + Succeeded + " AND finished_at < @succeeded_before) OR (state = " + DeadLettered + " AND finished_at < @dead_lettered_before);";

        // The poller's probe: has a job become due since the last probe? Locked rows are skipped, so a
        // probe never waits on a claim or on an enqueue that has not committed.
        Probe =
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM " + _jobs + " WITH (READPAST)\n"
            + "  WHERE state = " + Enqueued + " AND run_at > @since AND run_at <= @now) THEN 1 ELSE 0 END;";
    }

    public string Schema { get; }

    /// <summary>The table type of a job batch.</summary>
    public string JobRows { get; }

    /// <summary>The table type of a lease list.</summary>
    public string LeaseRows { get; }

    /// <summary>The table type of an id list.</summary>
    public string IdRows { get; }

    /// <summary>
    /// Takes the migration lock, then creates the schema and its version table if missing. Runs first
    /// in the migration's transaction, with @resource and @schema.
    /// </summary>
    public string MigrationPrologue { get; }

    /// <summary>Each migration in order, as a batch that applies it only when the version table lacks it. Migrations only ever add.</summary>
    public string[] Migrations { get; }

    public string Insert { get; }

    /// <summary>The orphan rescue, then the claim, in one batch; the claim's OUTPUT is its only result set.</summary>
    public string Claim { get; }

    public string Complete { get; }

    public string Retry { get; }

    public string DeadLetter { get; }

    public string Release { get; }

    public string Requeue { get; }

    public string Extend { get; }

    public string Get { get; }

    public string Prune { get; }

    public string Probe { get; }

    // sp_getapplock, owned by the migration's transaction, makes concurrent processes migrate one after
    // another; the later ones find every migration recorded and do nothing. DDL runs through EXEC, so
    // the batch compiles before the schema it creates exists.
    private string Prologue()
        => "DECLARE @result int;\n"
            + "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = -1;\n"
            + "IF @result < 0 THROW 50000, N'Could not take the job store''s migration lock.', 1;\n"
            + "IF SCHEMA_ID(@schema) IS NULL EXEC(N'CREATE SCHEMA " + _schema + "');\n"
            + "IF OBJECT_ID(N'" + _schema + ".[schema_version]', N'U') IS NULL\n"
            + "  EXEC(N'CREATE TABLE " + _schema + ".[schema_version] (version int NOT NULL PRIMARY KEY, applied_at datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME())');";

    private string Migration(int version, string[] statements)
    {
        var batch = new StringBuilder();
        batch.Append("IF NOT EXISTS (SELECT 1 FROM ").Append(_schema).Append(".[schema_version] WHERE version = ").Append(version).Append(")\nBEGIN\n");
        foreach (string statement in statements)
        {
            batch.Append("  EXEC(N'").Append(statement.Replace("'", "''", System.StringComparison.Ordinal)).Append("');\n");
        }
        batch.Append("  INSERT INTO ").Append(_schema).Append(".[schema_version] (version) VALUES (").Append(version).Append(");\nEND;");
        return batch.ToString();
    }

    // Migration 1: one row per job, state as a column. The filtered indexes keep the claim and the
    // orphan rescue on the small set of live rows, and the pruner on finished ones. The ready index is
    // unique: seq comes from one sequence, so no two ready jobs share a place in a queue's order. The
    // table types carry batches of jobs, leases and ids as table-valued parameters.
    private string[] CreateJobsTable() => new[]
    {
        "CREATE SEQUENCE " + _schema + ".[job_seq] AS bigint START WITH 1 INCREMENT BY 1",
        "CREATE TABLE " + _jobs + " ("
            + "id uniqueidentifier NOT NULL PRIMARY KEY, "
            + "kind nvarchar(" + JobLimits.MaxKindLength + ") NOT NULL, "
            + "queue nvarchar(" + JobLimits.MaxQueueLength + ") NOT NULL, "
            + "payload varbinary(max) NOT NULL, "
            + "state smallint NOT NULL DEFAULT " + Enqueued + " CHECK (state BETWEEN 0 AND 3), "
            + "attempt int NOT NULL DEFAULT 0, "
            + "max_attempts int NOT NULL CHECK (max_attempts >= 1), "
            + "seq bigint NOT NULL, "
            + "run_at datetime2(7) NOT NULL, "
            + "lease_until datetime2(7) NULL, "
            + "finished_at datetime2(7) NULL, "
            + "last_error nvarchar(" + JobLimits.MaxErrorLength + ") NULL)",
        "CREATE UNIQUE NONCLUSTERED INDEX jobs_ready ON " + _jobs + " (queue, run_at, seq) WHERE state = " + Enqueued,
        "CREATE NONCLUSTERED INDEX jobs_leased ON " + _jobs + " (queue, lease_until) WHERE state = " + Processing,
        "CREATE NONCLUSTERED INDEX jobs_finished ON " + _jobs + " (finished_at) WHERE state IN (" + Succeeded + ", " + DeadLettered + ")",
        "CREATE TYPE " + JobRows + " AS TABLE (ord int NOT NULL PRIMARY KEY, id uniqueidentifier NOT NULL, "
            + "kind nvarchar(" + JobLimits.MaxKindLength + ") NOT NULL, queue nvarchar(" + JobLimits.MaxQueueLength + ") NOT NULL, "
            + "payload varbinary(max) NOT NULL, max_attempts int NOT NULL)",
        "CREATE TYPE " + LeaseRows + " AS TABLE (id uniqueidentifier NOT NULL, attempt int NOT NULL, PRIMARY KEY (id, attempt))",
        "CREATE TYPE " + IdRows + " AS TABLE (id uniqueidentifier NOT NULL PRIMARY KEY)",
    };

    // Moves dead-lettered jobs back to ready; selects how many moved.
    private string RequeueSql()
        => "UPDATE j SET state = " + Enqueued + ", attempt = 0, run_at = @now, finished_at = NULL, seq = " + _next + "\n"
            + "FROM " + _jobs + " j INNER JOIN @ids i ON j.id = i.id WHERE j.state = " + DeadLettered + ";\n"
            + "SELECT @@ROWCOUNT;";

    // A processing job whose lease ran out is due again now, or dead-lettered when that claim was its
    // last attempt. Runs before each claim, on the claim's queue; rows another claim holds are skipped.
    private string Rescue()
        => "UPDATE " + _jobs + " WITH (READPAST, ROWLOCK) SET\n"
            + "  state = CASE WHEN attempt >= max_attempts THEN " + DeadLettered + " ELSE " + Enqueued + " END,\n"
            + "  run_at = @now, lease_until = NULL, seq = " + _next + ",\n"
            + "  finished_at = CASE WHEN attempt >= max_attempts THEN @now END,\n"
            + "  last_error = CASE WHEN attempt >= max_attempts THEN @orphan_error ELSE last_error END\n"
            + "WHERE queue = @queue AND state = " + Processing + " AND lease_until <= @now;";

    // Takes up to @max_count due jobs, oldest first, in one statement. UPDLOCK holds each row it reads
    // until the update, and READPAST skips rows another claim holds, so concurrent claims never wait on
    // or return the same job.
    private string TakeDue()
        => "WITH c AS (\n"
            + "  SELECT TOP (@max_count) id, kind, queue, payload, state, attempt, max_attempts, run_at, seq, lease_until\n"
            + "  FROM " + _jobs + " WITH (READPAST, UPDLOCK, ROWLOCK)\n"
            + "  WHERE queue = @queue AND state = " + Enqueued + " AND run_at <= @now\n"
            + "  ORDER BY run_at, seq\n"
            + ")\n"
            + "UPDATE c SET state = " + Processing + ", attempt = attempt + 1, lease_until = @lease_until\n"
            + "OUTPUT inserted.id, inserted.kind, inserted.queue, inserted.payload, inserted.attempt, inserted.max_attempts, inserted.run_at, inserted.seq;";

    private string UpdateHeld(string set) => "UPDATE " + _jobs + " SET " + set + "\nWHERE " + Held + ";";

    private string UpdateLeases(string set)
        => "UPDATE j SET " + set + "\n"
            + "FROM " + _jobs + " j INNER JOIN @leases l ON j.id = l.id AND j.attempt = l.attempt\n"
            + "WHERE " + HeldLeases + ";";
}
