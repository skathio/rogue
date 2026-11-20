namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>
/// The SQL the store runs, built once for its schema. The schema is the only text spliced in, and
/// <see cref="PostgresJobsOptions"/> accepts only names that need no escaping; every value is a
/// parameter. Job states are stored as the <see cref="JobState"/> numbers.
/// </summary>
internal sealed class PostgresSql
{
    // Rows the store's claim and pruner look for. A row moves through these as JobState does.
    private const string Enqueued = "0";
    private const string Processing = "1";
    private const string Succeeded = "2";
    private const string DeadLettered = "3";

    public PostgresSql(string schema)
    {
        Schema = schema;
        string s = "\"" + schema + "\"";
        Jobs = s + ".jobs";
        Channel = "\"" + schema + "\"";
        string sequence = s + ".job_seq";

        Migrations = new[] { CreateJobsTable(Jobs, sequence) };

        MigrationPrologue =
            "SELECT pg_advisory_xact_lock(1919381317, hashtext(@schema));\n"
            + "CREATE SCHEMA IF NOT EXISTS " + s + ";\n"
            + "CREATE TABLE IF NOT EXISTS " + s + ".schema_version (version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());";
        CurrentVersion = "SELECT coalesce(max(version), 0) FROM " + s + ".schema_version;";
        RecordMigration = "INSERT INTO " + s + ".schema_version (version) VALUES (@version);";

        Insert = "INSERT INTO " + Jobs + " (id, kind, queue, payload, max_attempts, run_at) VALUES (@id, @kind, @queue, @payload, @max_attempts, @now);";
        Copy = "COPY " + Jobs + " (id, kind, queue, payload, max_attempts, run_at) FROM STDIN (FORMAT BINARY)";
        Notify = "SELECT pg_notify(@channel, @queue);";

        Rescue = RescueSql(Jobs, sequence);
        Claim = ClaimSql(Jobs);

        // Every write for a claimed job applies only while that claim still holds it.
        const string held = "state = " + Processing + " AND attempt = @attempt AND lease_until > @now AND id = @id";
        Complete =
            "UPDATE " + Jobs + " j SET state = " + Succeeded + ", lease_until = NULL, finished_at = @now\n"
            + "FROM unnest(@ids, @attempts) AS l(id, attempt)\n"
            + "WHERE j.id = l.id AND j.attempt = l.attempt AND j.state = " + Processing + " AND j.lease_until > @now\n"
            + "RETURNING j.id, j.attempt;";
        Retry =
            "UPDATE " + Jobs + " SET state = " + Enqueued + ", run_at = @run_at, lease_until = NULL, seq = nextval('" + sequence + "'), last_error = @error\n"
            + "WHERE " + held + ";";
        DeadLetter =
            "UPDATE " + Jobs + " SET state = " + DeadLettered + ", lease_until = NULL, finished_at = @now, last_error = @error\n"
            + "WHERE " + held + ";";
        Release =
            "UPDATE " + Jobs + " SET state = " + Enqueued + ", attempt = attempt - 1, run_at = @run_at, lease_until = NULL, seq = nextval('" + sequence + "')\n"
            + "WHERE " + held + ";";
        Requeue = RequeueSql(Jobs, sequence);
        Extend =
            "UPDATE " + Jobs + " j SET lease_until = @lease_until\n"
            + "FROM unnest(@ids, @attempts) AS l(id, attempt)\n"
            + "WHERE j.id = l.id AND j.attempt = l.attempt AND j.state = " + Processing + " AND j.lease_until > @now;";
        Get = "SELECT kind, queue, state, attempt, max_attempts, run_at, last_error FROM " + Jobs + " WHERE id = @id;";
        Prune =
            "DELETE FROM " + Jobs + " WHERE id IN (SELECT id FROM " + Jobs + "\n"
            + "  WHERE (state = " + Succeeded + " AND finished_at < @succeeded_before) OR (state = " + DeadLettered + " AND finished_at < @dead_lettered_before)\n"
            + "  LIMIT @batch);";
        Listen = "LISTEN " + Channel + ";";
    }

    // Migration 1: one row per job, state as a column. The partial indexes keep the claim and the
    // orphan rescue on the small set of live rows, and the pruner on finished ones.
    private static string CreateJobsTable(string jobs, string sequence)
        => "CREATE SEQUENCE " + sequence + ";\n"
            + "CREATE TABLE " + jobs + " (\n"
            + "  id uuid PRIMARY KEY,\n"
            + "  kind text NOT NULL,\n"
            + "  queue text NOT NULL,\n"
            + "  payload bytea NOT NULL,\n"
            + "  state smallint NOT NULL DEFAULT " + Enqueued + " CHECK (state BETWEEN 0 AND 3),\n"
            + "  attempt integer NOT NULL DEFAULT 0,\n"
            + "  max_attempts integer NOT NULL CHECK (max_attempts >= 1),\n"
            + "  seq bigint NOT NULL DEFAULT nextval('" + sequence + "'),\n"
            + "  run_at timestamptz NOT NULL,\n"
            + "  lease_until timestamptz,\n"
            + "  finished_at timestamptz,\n"
            + "  last_error text\n"
            + ");\n"
            + "ALTER SEQUENCE " + sequence + " OWNED BY " + jobs + ".seq;\n"
            + "CREATE INDEX jobs_ready ON " + jobs + " (queue, run_at, seq) WHERE state = " + Enqueued + ";\n"
            + "CREATE INDEX jobs_leased ON " + jobs + " (queue, lease_until) WHERE state = " + Processing + ";\n"
            + "CREATE INDEX jobs_finished ON " + jobs + " (finished_at) WHERE state IN (" + Succeeded + ", " + DeadLettered + ");";

    // Moves dead-lettered jobs back to ready.
    private static string RequeueSql(string jobs, string sequence)
        => "UPDATE " + jobs + " SET state = " + Enqueued + ", attempt = 0, run_at = @now, finished_at = NULL, seq = nextval('" + sequence + "')\n"
            + "WHERE id = ANY(@ids) AND state = " + DeadLettered + ";";

    // A processing job whose lease ran out is due again now, or dead-lettered when that claim was its
    // last attempt. Runs before each claim, on the claim's queue.
    private static string RescueSql(string jobs, string sequence)
        => "UPDATE " + jobs + " j SET\n"
            + "  state = CASE WHEN j.attempt >= j.max_attempts THEN " + DeadLettered + " ELSE " + Enqueued + " END,\n"
            + "  run_at = @now, lease_until = NULL, seq = nextval('" + sequence + "'),\n"
            + "  finished_at = CASE WHEN j.attempt >= j.max_attempts THEN @now END,\n"
            + "  last_error = CASE WHEN j.attempt >= j.max_attempts THEN @orphan_error ELSE j.last_error END\n"
            + "FROM (SELECT id FROM " + jobs + " WHERE queue = @queue AND state = " + Processing + " AND lease_until <= @now FOR UPDATE SKIP LOCKED) o\n"
            + "WHERE j.id = o.id;";

    // Takes up to @max_count due jobs, oldest first; rows another claim has locked are skipped, so
    // concurrent claims never wait on or return the same job.
    private static string ClaimSql(string jobs)
        => "UPDATE " + jobs + " j SET state = " + Processing + ", attempt = j.attempt + 1, lease_until = @lease_until\n"
            + "FROM (SELECT id FROM " + jobs + " WHERE queue = @queue AND state = " + Enqueued + " AND run_at <= @now\n"
            + "      ORDER BY run_at, seq LIMIT @max_count FOR UPDATE SKIP LOCKED) c\n"
            + "WHERE j.id = c.id\n"
            + "RETURNING j.id, j.kind, j.queue, j.payload, j.attempt, j.max_attempts, j.run_at, j.seq;";

    public string Schema { get; }

    public string Jobs { get; }

    /// <summary>The LISTEN/NOTIFY channel, quoted as LISTEN needs it; pg_notify takes <see cref="Schema"/> unquoted.</summary>
    public string Channel { get; }

    /// <summary>Each migration in order; migration n is at index n - 1. Migrations only ever add.</summary>
    public string[] Migrations { get; }

    public string MigrationPrologue { get; }

    public string CurrentVersion { get; }

    public string RecordMigration { get; }

    public string Insert { get; }

    public string Copy { get; }

    public string Notify { get; }

    public string Rescue { get; }

    public string Claim { get; }

    public string Complete { get; }

    public string Retry { get; }

    public string DeadLetter { get; }

    public string Release { get; }

    public string Requeue { get; }

    public string Extend { get; }

    public string Get { get; }

    public string Prune { get; }

    public string Listen { get; }
}
