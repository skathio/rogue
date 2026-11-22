namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>
/// The SQL for a message consumer's inbox, built once for the store's schema; every value is a
/// parameter. One row per message id: a claim holds it while lease_until is in the future, and
/// handled_at marks it handled.
/// </summary>
internal sealed class PostgresInboxSql
{
    // The claim's answers, read as MessageClaim.
    private const string Claimed = "0";
    private const string Handled = "1";
    private const string Busy = "2";

    /// <param name="schema">The quoted schema name.</param>
    public PostgresInboxSql(string schema)
    {
        string inbox = schema + ".inbox";

        // Migration 3: the inbox. The partial indexes keep the pruner on handled rows and on claims.
        Migration =
            "CREATE TABLE " + inbox + " (\n"
            + "  message_id text PRIMARY KEY,\n"
            + "  lease_until timestamptz,\n"
            + "  handled_at timestamptz\n"
            + ");\n"
            + "CREATE INDEX inbox_handled ON " + inbox + " (handled_at) WHERE handled_at IS NOT NULL;\n"
            + "CREATE INDEX inbox_claimed ON " + inbox + " (lease_until) WHERE handled_at IS NULL;";

        // Inserts the claim, or takes over one whose lease ran out, and answers Claimed; otherwise
        // answers from the row. A concurrent claim of the same id waits for the other to commit, then
        // finds its row, which this statement's snapshot cannot see: no row at all then means Busy.
        Claim =
            "WITH claim AS (\n"
            + "  INSERT INTO " + inbox + " AS i (message_id, lease_until) VALUES (@id, @lease_until)\n"
            + "  ON CONFLICT (message_id) DO UPDATE SET lease_until = excluded.lease_until\n"
            + "  WHERE i.handled_at IS NULL AND i.lease_until <= @now\n"
            + "  RETURNING " + Claimed + " AS outcome\n"
            + ")\n"
            + "SELECT outcome FROM claim\n"
            + "UNION ALL\n"
            + "SELECT CASE WHEN handled_at IS NULL THEN " + Busy + " ELSE " + Handled + " END FROM " + inbox + "\n"
            + "WHERE message_id = @id AND NOT EXISTS (SELECT 1 FROM claim);";
        // Complete and release act on the claim whose lease end is @lease_until only, so a delivery
        // whose lease ran out cannot touch the claim of one that took the id over. With no row, no
        // claim holds the id and complete records it handled.
        Complete =
            "INSERT INTO " + inbox + " AS i (message_id, handled_at) VALUES (@id, @now)\n"
            + "ON CONFLICT (message_id) DO UPDATE SET handled_at = excluded.handled_at, lease_until = NULL\n"
            + "WHERE i.handled_at IS NULL AND i.lease_until = @lease_until;";
        Release = "DELETE FROM " + inbox + " WHERE message_id = @id AND handled_at IS NULL AND lease_until = @lease_until;";

        // Handled ids past retention, and claims whose lease ended that long ago (their message never
        // came back), a batch per statement.
        Prune =
            "DELETE FROM " + inbox + " WHERE message_id IN (SELECT message_id FROM " + inbox + "\n"
            + "  WHERE handled_at < @before OR (handled_at IS NULL AND lease_until < @before)\n"
            + "  LIMIT @batch);";
    }

    public string Migration { get; }

    /// <summary>Selects the claim's answer as a <see cref="MessageClaim"/> number, or no row when another claim is committing.</summary>
    public string Claim { get; }

    public string Complete { get; }

    public string Release { get; }

    public string Prune { get; }
}
