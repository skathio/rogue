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

    // The rows that hold a unique key: pending ones.
    private const string PendingUnique = "unique_key IS NOT NULL AND state IN (" + Enqueued + ", " + Processing + ")";

    private readonly string _schema;
    private readonly string _jobs;
    private readonly string _next;
    private readonly string _runs;
    private readonly string _leaders;

    public SqlServerSql(string schema)
    {
        _schema = "[" + schema + "]";
        _jobs = _schema + ".[jobs]";
        _next = "NEXT VALUE FOR " + _schema + ".[job_seq]";
        _runs = _schema + ".[recurring_runs]";
        _leaders = _schema + ".[leaders]";
        Schema = schema;
        JobRows = _schema + ".[job_batch]";
        LeaseRows = _schema + ".[lease_rows]";
        IdRows = _schema + ".[id_rows]";
        MigrationPrologue = Prologue();
        Migrations = new[] { Migration(1, CreateJobsTable()), Migration(2, AddSchedulingTables()) };

        // One statement for the whole batch: a duplicate id fails it all. The sequence numbers follow
        // the batch order, so jobs enqueued together are claimed in that order.
        Insert =
            "INSERT INTO " + _jobs + " (id, kind, queue, payload, max_attempts, seq, run_at)\n"
            + "SELECT id, kind, queue, payload, max_attempts, " + _next + " OVER (ORDER BY ord), COALESCE(run_at, @now) FROM @jobs;";
        EnqueueOnce = EnqueueOnceSql();
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

        PruneRuns = "DELETE FROM " + _runs + " WHERE run_at < @before;";

        // MERGE under HOLDLOCK takes the lock when it is free, ours, or its holder's lease has run out,
        // and outputs a row only then; two hosts racing for a free lock cannot both insert it.
        AcquireLeadership =
            "MERGE " + _leaders + " WITH (HOLDLOCK) AS l\n"
            + "USING (SELECT @name AS name) AS s ON l.name = s.name\n"
            + "WHEN MATCHED AND (l.owner = @owner OR l.lease_until <= @now) THEN UPDATE SET owner = @owner, lease_until = @lease_until\n"
            + "WHEN NOT MATCHED THEN INSERT (name, owner, lease_until) VALUES (@name, @owner, @lease_until)\n"
            + "OUTPUT inserted.owner;";
        ReleaseLeadership = "DELETE FROM " + _leaders + " WHERE name = @name AND owner = @owner;";

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

    /// <summary>Stores one keyed job unless its run or its unique key is already held; selects the id of the job that stands for it.</summary>
    public string EnqueueOnce { get; }

    public string PruneRuns { get; }

    /// <summary>Outputs the owner when the lock was taken or renewed, and no row otherwise.</summary>
    public string AcquireLeadership { get; }

    public string ReleaseLeadership { get; }

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

    // Migration 2: delayed and recurring jobs, unique keys and the leader lock. A unique key is held by
    // the pending jobs of its kind only, under a filtered unique index; a recurring run is stored once
    // per task key and run time, which the run ledger keeps apart from the job's own run_at, since a
    // retry moves that. The new batch type carries each job's run time.
    private string[] AddSchedulingTables() => new[]
    {
        "ALTER TABLE " + _jobs + " ADD unique_key nvarchar(" + JobLimits.MaxKeyLength + ") NULL",
        "CREATE UNIQUE NONCLUSTERED INDEX jobs_unique_key ON " + _jobs + " (kind, unique_key) WHERE " + PendingUnique,
        "CREATE TABLE " + _runs + " ("
            + "task_key nvarchar(" + JobLimits.MaxKeyLength + ") NOT NULL, "
            + "run_at datetime2(7) NOT NULL, "
            + "job_id uniqueidentifier NOT NULL, "
            + "CONSTRAINT recurring_runs_once UNIQUE (task_key, run_at))",
        "CREATE TABLE " + _leaders + " ("
            + "name nvarchar(" + JobLimits.MaxKeyLength + ") NOT NULL PRIMARY KEY, "
            + "owner nvarchar(" + JobLimits.MaxKeyLength + ") NOT NULL, "
            + "lease_until datetime2(7) NOT NULL)",
        "CREATE TYPE " + JobRows + " AS TABLE (ord int NOT NULL PRIMARY KEY, id uniqueidentifier NOT NULL, "
            + "kind nvarchar(" + JobLimits.MaxKindLength + ") NOT NULL, queue nvarchar(" + JobLimits.MaxQueueLength + ") NOT NULL, "
            + "payload varbinary(max) NOT NULL, max_attempts int NOT NULL, run_at datetime2(7) NULL)",
    };

    // One transaction: the run ledger first, then the pending holder of the unique key, then the
    // insert. UPDLOCK with HOLDLOCK keeps the looked-up key range locked until the commit, so a
    // concurrent enqueue of the same run or key waits and then finds this one's job.
    private string EnqueueOnceSql()
        => "SET XACT_ABORT ON;\n"
            + "BEGIN TRANSACTION;\n"
            + "DECLARE @stored uniqueidentifier;\n"
            + "IF @task_key IS NOT NULL\n"
            + "  SELECT @stored = job_id FROM " + _runs + " WITH (UPDLOCK, HOLDLOCK) WHERE task_key = @task_key AND run_at = @run_at;\n"
            + "IF @stored IS NULL AND @unique_key IS NOT NULL\n"
            + "  SELECT @stored = id FROM " + _jobs + " WITH (UPDLOCK, HOLDLOCK) WHERE kind = @kind AND unique_key = @unique_key AND state IN (" + Enqueued + ", " + Processing + ");\n"
            + "IF @stored IS NULL\n"
            + "BEGIN\n"
            + "  INSERT INTO " + _jobs + " (id, kind, queue, payload, max_attempts, seq, run_at, unique_key)\n"
            + "  VALUES (@id, @kind, @queue, @payload, @max_attempts, " + _next + ", @run_at, @unique_key);\n"
            + "  SET @stored = @id;\n"
            + "END;\n"
            + "IF @task_key IS NOT NULL AND NOT EXISTS (SELECT 1 FROM " + _runs + " WHERE task_key = @task_key AND run_at = @run_at)\n"
            + "  INSERT INTO " + _runs + " (task_key, run_at, job_id) VALUES (@task_key, @run_at, @stored);\n"
            + "COMMIT TRANSACTION;\n"
            + "SELECT @stored;";

    // Moves dead-lettered jobs back to ready, skipping one whose unique key a pending job holds and,
    // of several sharing a kind and unique key, all but one; selects how many moved. The chosen ids
    // go through a table variable, since NEXT VALUE FOR cannot share a statement with a window function.
    private string RequeueSql()
        => "DECLARE @move TABLE (id uniqueidentifier NOT NULL PRIMARY KEY);\n"
            + "INSERT INTO @move (id)\n"
            + "SELECT c.id FROM (\n"
            + "  SELECT j.id, j.kind, j.unique_key, ROW_NUMBER() OVER (PARTITION BY j.kind, j.unique_key ORDER BY j.id) AS n\n"
            + "  FROM " + _jobs + " j INNER JOIN @ids i ON j.id = i.id WHERE j.state = " + DeadLettered + ") c\n"
            + "WHERE c.unique_key IS NULL OR (c.n = 1 AND NOT EXISTS (\n"
            + "  SELECT 1 FROM " + _jobs + " o WHERE o.kind = c.kind AND o.unique_key = c.unique_key AND o.state IN (" + Enqueued + ", " + Processing + ")));\n"
            + "UPDATE j SET state = " + Enqueued + ", attempt = 0, run_at = @now, finished_at = NULL, seq = " + _next + "\n"
            + "FROM " + _jobs + " j INNER JOIN @move m ON j.id = m.id WHERE j.state = " + DeadLettered + ";\n"
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
