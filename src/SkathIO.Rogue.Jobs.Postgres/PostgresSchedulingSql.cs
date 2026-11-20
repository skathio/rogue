namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>
/// The SQL for delayed and recurring jobs, unique keys and the leader lock, built once for the
/// store's schema; every value is a parameter. A unique key is held by the pending (enqueued or
/// processing) jobs of its kind only, under a partial unique index. A recurring run is stored once per
/// task key and run time: the run ledger keeps that pair apart from the job's own run_at, which a
/// retry moves.
/// </summary>
internal sealed class PostgresSchedulingSql
{
    // The rows that hold a unique key. The index predicate and the ON CONFLICT target spell it the
    // same, so Postgres matches the one to the other.
    private const string PendingUnique = "unique_key IS NOT NULL AND state IN (0, 1)";

    /// <param name="schema">The quoted schema name.</param>
    /// <param name="jobs">The jobs table, schema-qualified.</param>
    public PostgresSchedulingSql(string schema, string jobs)
    {
        string runs = schema + ".recurring_runs";
        string leaders = schema + ".leaders";

        // Migration 2: the unique key column and its index, the run ledger and the leader lock.
        Migration =
            "ALTER TABLE " + jobs + " ADD COLUMN unique_key text;\n"
            + "CREATE UNIQUE INDEX jobs_unique_key ON " + jobs + " (kind, unique_key) WHERE " + PendingUnique + ";\n"
            + "CREATE TABLE " + runs + " (\n"
            + "  task_key text NOT NULL,\n"
            + "  run_at timestamptz NOT NULL,\n"
            + "  job_id uuid NOT NULL,\n"
            + "  UNIQUE (task_key, run_at)\n"
            + ");\n"
            + "CREATE TABLE " + leaders + " (\n"
            + "  name text PRIMARY KEY,\n"
            + "  owner text NOT NULL,\n"
            + "  lease_until timestamptz NOT NULL\n"
            + ");";

        // A keyed insert stores nothing, and returns no row, while a pending job of its kind holds its
        // unique key; a duplicate id still fails it.
        InsertUnique =
            "INSERT INTO " + jobs + " (id, kind, queue, payload, max_attempts, run_at, unique_key) VALUES (@id, @kind, @queue, @payload, @max_attempts, @run_at, @unique_key)\n"
            + "ON CONFLICT (kind, unique_key) WHERE " + PendingUnique + " DO NOTHING\n"
            + "RETURNING id;";
        FindUnique = "SELECT id FROM " + jobs + " WHERE kind = @kind AND unique_key = @unique_key AND " + PendingUnique + ";";

        // An insert into the ledger waits for a concurrent one of the same run, then returns no row.
        ClaimRun =
            "INSERT INTO " + runs + " (task_key, run_at, job_id) VALUES (@task_key, @run_at, @id)\n"
            + "ON CONFLICT (task_key, run_at) DO NOTHING RETURNING job_id;";
        FindRun = "SELECT job_id FROM " + runs + " WHERE task_key = @task_key AND run_at = @run_at;";
        PointRun = "UPDATE " + runs + " SET job_id = @id WHERE task_key = @task_key AND run_at = @run_at;";
        PruneRuns = "DELETE FROM " + runs + " WHERE run_at < @before;";

        // Takes the lock when it is free, ours, or its holder's lease has run out; returns a row only then.
        AcquireLeadership =
            "INSERT INTO " + leaders + " AS l (name, owner, lease_until) VALUES (@name, @owner, @lease_until)\n"
            + "ON CONFLICT (name) DO UPDATE SET owner = excluded.owner, lease_until = excluded.lease_until\n"
            + "WHERE l.owner = excluded.owner OR l.lease_until <= @now\n"
            + "RETURNING l.owner;";
        ReleaseLeadership = "DELETE FROM " + leaders + " WHERE name = @name AND owner = @owner;";
    }

    public string Migration { get; }

    public string InsertUnique { get; }

    public string FindUnique { get; }

    public string ClaimRun { get; }

    public string FindRun { get; }

    public string PointRun { get; }

    public string PruneRuns { get; }

    public string AcquireLeadership { get; }

    public string ReleaseLeadership { get; }
}
