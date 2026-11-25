using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class Recounts(NpgsqlDataSource db)
{
    private static byte[] Digest<T>(string kind, T input) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + JsonSerializer.Serialize(input, Wire.Json)));

    private const string TargetColumns = """
        t.position_id,t.purchase_id,t.purchase_reference,t.delivery_reference,t.item_name,t.location_id,t.location_code,
        t.purchase_revision,t.catalog_revision,t.location_revision,t.quantity
        """;
    private static RecountTarget Target(NpgsqlDataReader r) => new(r.GetGuid(0), Optional<Guid>(r, 1),
        Text(r, 2), Text(r, 3), Text(r, 4), Optional<Guid>(r, 5), Text(r, 6), Optional<int>(r, 7), Optional<int>(r, 8), Optional<int>(r, 9), Optional<int>(r, 10));
    private static string? Text(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? TrimPage<T>(List<T> rows, int limit, Func<T, Guid> key)
    {
        if (rows.Count <= limit) return null;
        rows.RemoveAt(limit);
        return key(rows[^1]);
    }
    private static void PageSize(int limit)
    {
        if (limit is < 1 or > 100) throw new Fault(400, "invalid_recount_page", "Choose a page size from 1 to 100.");
    }
    private static Fault BadCursor() => new(400, "invalid_recount_cursor", "Use the next cursor returned for this recount view.");
    private static async Task Execute(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = Query(c, tx, sql, args);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RecountPreviewPage> Freeze(RecountPreviewRequest request, CancellationToken ct)
    {
        var input = request.Normalized();
        var digest = Digest("recount-preview", input);
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Migration.CheckWithin(c, tx, ct);
        await Execute(c, tx, "SELECT pg_advisory_xact_lock(hashtextextended($1::text,83192))", ct, input.PreviewId);
        await using var prior = Query(c, tx, "SELECT digest FROM brickvault.recount_previews WHERE id=$1", input.PreviewId);
        if (await prior.ExecuteScalarAsync(ct) is byte[] old)
        {
            if (!CryptographicOperations.FixedTimeEquals(old, digest))
                throw new Fault(409, "recount_preview_mismatch", "This preview ID already identifies a different inventory scope.");
        }
        else
        {
            await Execute(c, tx, "INSERT INTO brickvault.recount_previews(id,mode,query,item_type,location_id,digest) VALUES ($1,$2,$3,$4,$5,$6)",
                ct, input.PreviewId, input.Scope, Nullable(NpgsqlDbType.Text, input.Q), Nullable(NpgsqlDbType.Text, input.ItemType),
                Nullable(NpgsqlDbType.Uuid, input.LocationId), digest);
            const string columns = """
                INSERT INTO brickvault.recount_preview_targets(preview_id,position_id,purchase_id,catalog_id,location_id,
                    purchase_reference,delivery_reference,item_name,location_code,purchase_revision,catalog_revision,location_revision,quantity)
                """;
            const string facts = "p.id,p.purchase_id,p.catalog_id,p.location_id,buy.reference AS purchase_reference,d.reference AS delivery_reference,c.name,loc.code,buy.revision AS purchase_revision,c.revision AS catalog_revision,loc.revision AS location_revision,p.quantity ";
            if (input.Scope == "selected")
                await Execute(c, tx, columns + "\n" + """
                    SELECT $1,ids.id,f.purchase_id,f.catalog_id,f.location_id,f.purchase_reference,f.delivery_reference,f.name,
                        f.code,f.purchase_revision,f.catalog_revision,f.location_revision,f.quantity
                    FROM unnest($2::uuid[]) ids(id) LEFT JOIN (
                    """ + " SELECT " + facts + PositionRows.RegisterFrom + " WHERE p.id=ANY($2)) f ON ids.id=f.id", ct, input.PreviewId, input.PositionIds);
            else
                await Execute(c, tx, columns + " SELECT $6," + facts + StockRegister.Filtered,
                    ct, [.. StockRegister.Filters(input.Q, input.ItemType, input.LocationId, null), input.PreviewId]);
        }
        var page = await Preview(c, tx, input.PreviewId, null, 25, ct);
        await tx.CommitAsync(ct);
        return page;
    }

    public async Task<RecountPreviewPage> Preview(Guid id, Guid? after, int limit, CancellationToken ct)
    {
        PageSize(limit);
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Migration.CheckWithin(c, tx, ct);
        return await Preview(c, tx, id, after, limit, ct);
    }
    private static async Task<RecountPreviewPage> Preview(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, Guid? after, int limit, CancellationToken ct)
    {
        var heads = await Rows(c, tx, """
            SELECT mode,query,item_type,location_id,frozen_at,
                (SELECT count(*) FROM brickvault.recount_preview_targets WHERE preview_id=$1),
                ($2::uuid IS NULL OR EXISTS(SELECT 1 FROM brickvault.recount_preview_targets WHERE preview_id=$1 AND position_id=$2))
            FROM brickvault.recount_previews WHERE id=$1
            """, r => (Mode: r.GetString(0), Q: Text(r, 1), Type: Text(r, 2), Bin: Optional<Guid>(r, 3), Time: r.GetDateTime(4), Total: r.GetInt64(5), Cursor: r.GetBoolean(6)), ct, id, Nullable(NpgsqlDbType.Uuid, after));
        if (heads.Count == 0) throw new Fault(404, "unknown_recount_preview", "The requested recount preview was not found.");
        var h = heads[0];
        if (!h.Cursor) throw BadCursor();
        var rows = await Rows(c, tx, "SELECT " + TargetColumns + " FROM brickvault.recount_preview_targets t " +
            "WHERE preview_id=$1 AND ($2::uuid IS NULL OR position_id>$2) ORDER BY position_id LIMIT $3", Target, ct, id, Nullable(NpgsqlDbType.Uuid, after), limit + 1);
        return new("recount-preview", id, h.Mode, h.Q, h.Type, h.Bin, h.Time, true, h.Total, TrimPage(rows, limit, t => t.PositionId), rows);
    }

    public async Task<Receipt> Confirm(RecountCommand command, Guid correlation, CancellationToken ct)
    {
        if (command.CommandId == Guid.Empty || command.PreviewId == Guid.Empty) throw new Fault(400, "invalid_recount", "Confirm an existing frozen preview.");
        var id = command.PreviewId;
        var digest = Digest("recount", command);
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Migration.CheckWithin(c, tx, ct);
        if (await CommandJournal.Replay(c, tx, command.CommandId, digest, ct) is { } replay) return replay;
        // Serialize batches, then hold their stock and reference revisions until the results and audit receipt commit.
        await Execute(c, tx, "SELECT pg_advisory_xact_lock(471139,8)", ct);
        await using var state = Query(c, tx, """
            SELECT CASE WHEN EXISTS(SELECT 1 FROM brickvault.recount_batches WHERE id=$1) THEN 'used'
                 WHEN EXISTS(SELECT 1 FROM brickvault.recount_preview_targets WHERE preview_id=$1) THEN 'ready' ELSE 'empty' END
            FROM brickvault.recount_previews WHERE id=$1
            """, id);
        switch (await state.ExecuteScalarAsync(ct))
        {
            case null: throw new Fault(404, "unknown_recount_preview", "The requested recount preview was not found.");
            case "empty": throw new Fault(409, "empty_recount_preview", "This preview contains no positions to queue.");
            case "used": throw new Fault(409, "recount_preview_used", "This preview already has a recount receipt.");
        }
        await Execute(c, tx, "SELECT id FROM brickvault.purchases WHERE id IN (SELECT purchase_id FROM brickvault.recount_preview_targets WHERE preview_id=$1) ORDER BY id FOR SHARE", ct, id);
        await Execute(c, tx, "SELECT id FROM brickvault.catalog WHERE id IN (SELECT catalog_id FROM brickvault.recount_preview_targets WHERE preview_id=$1) ORDER BY id FOR SHARE", ct, id);
        await Execute(c, tx, "SELECT id FROM brickvault.locations WHERE id IN (SELECT location_id FROM brickvault.recount_preview_targets WHERE preview_id=$1) ORDER BY id FOR SHARE", ct, id);
        await Execute(c, tx, "INSERT INTO brickvault.recount_batches(id,command_id) VALUES ($1,$2)", ct, id, command.CommandId);
        await Execute(c, tx, """
            INSERT INTO brickvault.recount_results(batch_id,position_id,outcome,queue_batch_id)
            SELECT $1,position_id,outcome,CASE outcome WHEN 'queued' THEN $1 WHEN 'already-queued' THEN prior END FROM (
              SELECT t.position_id,q.batch_id AS prior,
                CASE WHEN p.id IS NULL OR t.purchase_revision IS NULL OR p.quantity=0 THEN 'unavailable'
                     WHEN buy.revision<>t.purchase_revision OR c.revision<>t.catalog_revision OR loc.revision<>t.location_revision THEN 'changed'
                     WHEN q.batch_id IS NOT NULL THEN 'already-queued' ELSE 'queued' END AS outcome
              FROM brickvault.recount_preview_targets t
              LEFT JOIN brickvault.current_placement_stock p ON p.id=t.position_id
              LEFT JOIN brickvault.purchases buy ON buy.id=p.purchase_id
              LEFT JOIN brickvault.catalog c ON c.id=p.catalog_id
              LEFT JOIN brickvault.locations loc ON loc.id=p.location_id
              LEFT JOIN brickvault.recount_tasks q ON q.position_id=t.position_id AND q.observation_id IS NULL
              WHERE t.preview_id=$1
            ) decisions
            """, ct, id);
        var receipt = new Receipt(command.CommandId, id, 1, "recount", correlation);
        await CommandJournal.Append(c, tx, receipt, digest, "record-receiving", ct);
        await tx.CommitAsync(ct);
        return receipt;
    }

    private const string Counts = """
        count(*) AS total,count(*) FILTER (WHERE outcome='queued') AS queued,count(*) FILTER (WHERE outcome='changed') AS changed,
        count(*) FILTER (WHERE outcome='already-queued') AS already_queued,count(*) FILTER (WHERE outcome='unavailable') AS unavailable
        """;
    public async Task<RecountBatchPage> Batch(Guid id, Guid? after, int limit, CancellationToken ct)
    {
        PageSize(limit);
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Migration.CheckWithin(c, tx, ct);
        var heads = await Rows(c, tx, "SELECT b.command_id,j.correlation_id,transaction_timestamp(),summary.*," +
            "($2::uuid IS NULL OR EXISTS(SELECT 1 FROM brickvault.recount_results WHERE batch_id=$1 AND position_id=$2)) " +
            "FROM brickvault.recount_batches b JOIN brickvault.commands j ON j.command_id=b.command_id CROSS JOIN " +
            "(SELECT " + Counts + " FROM brickvault.recount_results WHERE batch_id=$1) summary WHERE b.id=$1",
            r => (Command: r.GetGuid(0), Correlation: r.GetGuid(1), Time: r.GetDateTime(2), Total: r.GetInt64(3), Queued: r.GetInt64(4),
                Changed: r.GetInt64(5), Already: r.GetInt64(6), Unavailable: r.GetInt64(7), Cursor: r.GetBoolean(8)), ct, id, Nullable(NpgsqlDbType.Uuid, after));
        if (heads.Count == 0) throw new Fault(404, "unknown_recount_batch", "The requested recount receipt was not found.");
        var h = heads[0];
        if (!h.Cursor) throw BadCursor();
        var rows = await Rows(c, tx, "SELECT " + TargetColumns + ",r.outcome,r.queue_batch_id,q.observation_id, " + """
            CASE WHEN q.batch_id IS NULL THEN NULL WHEN q.observation_id IS NOT NULL THEN 'witnessed'
                 WHEN stock.quantity>0 THEN 'pending' ELSE 'depleted' END
            FROM brickvault.recount_results r JOIN brickvault.recount_preview_targets t ON t.preview_id=r.batch_id AND t.position_id=r.position_id
            LEFT JOIN brickvault.recount_tasks q ON q.batch_id=r.queue_batch_id AND q.position_id=r.position_id
            LEFT JOIN brickvault.current_placement_stock stock ON stock.id=q.position_id
            WHERE r.batch_id=$1 AND ($2::uuid IS NULL OR r.position_id>$2) ORDER BY r.position_id LIMIT $3
            """, r => new RecountResult(Target(r), r.GetString(11), Optional<Guid>(r, 12), Optional<Guid>(r, 13), Text(r, 14)), ct, id, Nullable(NpgsqlDbType.Uuid, after), limit + 1);
        return new("recount-batch", id, h.Command, h.Correlation, h.Time, true, h.Total, h.Queued, h.Changed, h.Already, h.Unavailable, TrimPage(rows, limit, r => r.Target.PositionId), rows);
    }

    public async Task<RecountQueuePage> Queue(Guid? after, int limit, CancellationToken ct)
    {
        PageSize(limit);
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Migration.CheckWithin(c, tx, ct);
        const string actionable = """
            FROM brickvault.recount_tasks q
            JOIN brickvault.current_placement_stock p ON p.id=q.position_id AND p.quantity>0
            JOIN brickvault.recount_preview_targets t ON t.preview_id=q.batch_id AND t.position_id=q.position_id
            WHERE q.observation_id IS NULL
            """;
        var rows = await Rows(c, tx, "SELECT " + TargetColumns + ",q.batch_id " + actionable +
            " AND ($1::uuid IS NULL OR q.position_id>$1) ORDER BY q.position_id LIMIT $2",
            r => new RecountQueueEntry(r.GetGuid(11), Target(r), true), ct, Nullable(NpgsqlDbType.Uuid, after), limit + 1);
        var total = (await Rows(c, tx, "SELECT transaction_timestamp(),count(*) " + actionable,
            r => (Time: r.GetDateTime(0), Count: r.GetInt64(1)), ct)).Single();
        return new("recount-queue", total.Time, true, total.Count, TrimPage(rows, limit, r => r.Target.PositionId), rows);
    }

    public async Task<RecountBatchList> Batches(Guid? after, int limit, CancellationToken ct)
    {
        PageSize(limit);
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Migration.CheckWithin(c, tx, ct);
        DateTime? marker = null;
        if (after is Guid cursor)
        {
            await using var find = Query(c, tx, "SELECT queued_at FROM brickvault.recount_batches WHERE id=$1", cursor);
            marker = await find.ExecuteScalarAsync(ct) as DateTime? ?? throw BadCursor();
        }
        var rows = await Rows(c, tx, "SELECT b.id,b.command_id,b.queued_at,right_side.* FROM (" +
            "SELECT id,command_id,queued_at FROM brickvault.recount_batches WHERE ($1::timestamptz IS NULL OR (queued_at,id)<($1,$2)) " +
            "ORDER BY queued_at DESC,id DESC LIMIT $3) b CROSS JOIN LATERAL (SELECT " + Counts +
            " FROM brickvault.recount_results WHERE batch_id=b.id) right_side ORDER BY b.queued_at DESC,b.id DESC",
            r => new RecountSummary(r.GetGuid(0), r.GetGuid(1), r.GetDateTime(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7)),
            ct, Nullable(NpgsqlDbType.TimestampTz, marker), Nullable(NpgsqlDbType.Uuid, after), limit + 1);
        await using var clock = Query(c, tx, "SELECT transaction_timestamp()");
        return new("recount-batches", (DateTime)(await clock.ExecuteScalarAsync(ct))!, true, TrimPage(rows, limit, r => r.BatchId), rows);
    }
}
