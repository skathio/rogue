namespace SkathIO.Rogue.Jobs.SqlServer;

/// <summary>
/// The SQL for a message consumer's inbox, built once for the store's schema; every value is a
/// parameter. One row per message id: a claim holds it while lease_until is in the future, and
/// handled_at marks it handled.
/// </summary>
internal sealed class SqlServerInboxSql
{
    // The claim's answers, read as MessageClaim.
    private const string Claimed = "0";
    private const string Handled = "1";
    private const string Busy = "2";

    /// <param name="schema">The bracketed schema name.</param>
    public SqlServerInboxSql(string schema)
    {
        string inbox = schema + ".[inbox]";

        // Migration 3: the inbox. A binary collation compares message ids exactly, as Postgres does;
        // the database's default collation may ignore case. The filtered indexes keep the pruner on
        // handled rows and on claims.
        Migration = new[]
        {
            "CREATE TABLE " + inbox + " ("
                + "message_id nvarchar(" + JobLimits.MaxKeyLength + ") COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY, "
                + "lease_until datetime2(7) NULL, "
                + "handled_at datetime2(7) NULL)",
            "CREATE NONCLUSTERED INDEX inbox_handled ON " + inbox + " (handled_at) WHERE handled_at IS NOT NULL",
            "CREATE NONCLUSTERED INDEX inbox_claimed ON " + inbox + " (lease_until) WHERE handled_at IS NULL",
        };

        // One transaction: UPDLOCK with HOLDLOCK keeps the id's key range locked until the commit, so
        // a concurrent claim of the same id waits and then finds this one's row.
        Claim =
            "SET XACT_ABORT ON;\n"
            + "BEGIN TRANSACTION;\n"
            + "DECLARE @found bit = 0, @outcome int = " + Claimed + ";\n"
            + "SELECT @found = 1, @outcome = CASE WHEN handled_at IS NOT NULL THEN " + Handled + " WHEN lease_until > @now THEN " + Busy + " ELSE " + Claimed + " END\n"
            + "FROM " + inbox + " WITH (UPDLOCK, HOLDLOCK) WHERE message_id = @id;\n"
            + "IF @found = 0\n"
            + "  INSERT INTO " + inbox + " (message_id, lease_until) VALUES (@id, @lease_until);\n"
            + "IF @found = 1 AND @outcome = " + Claimed + "\n"
            + "  UPDATE " + inbox + " SET lease_until = @lease_until WHERE message_id = @id;\n"
            + "COMMIT TRANSACTION;\n"
            + "SELECT @outcome;";
        // Complete and release act on the claim whose lease end is @lease_until only, so a delivery
        // whose lease ran out cannot touch the claim of one that took the id over. With no row, no
        // claim holds the id and complete records it handled.
        Complete =
            "MERGE " + inbox + " WITH (HOLDLOCK) AS i\n"
            + "USING (SELECT @id AS message_id) AS m ON i.message_id = m.message_id\n"
            + "WHEN MATCHED AND i.handled_at IS NULL AND i.lease_until = @lease_until THEN UPDATE SET handled_at = @now, lease_until = NULL\n"
            + "WHEN NOT MATCHED THEN INSERT (message_id, handled_at) VALUES (@id, @now);";
        Release = "DELETE FROM " + inbox + " WHERE message_id = @id AND handled_at IS NULL AND lease_until = @lease_until;";

        // Handled ids past retention, and claims whose lease ended that long ago (their message never
        // came back), a batch per statement.
        Prune =
            "DELETE TOP (@batch) FROM " + inbox + "\n"
            + "WHERE handled_at < @before OR (handled_at IS NULL AND lease_until < @before);";
    }

    /// <summary>The statements of migration 3, each run on its own.</summary>
    public string[] Migration { get; }

    /// <summary>Selects the claim's answer as a <see cref="MessageClaim"/> number.</summary>
    public string Claim { get; }

    public string Complete { get; }

    public string Release { get; }

    public string Prune { get; }
}
