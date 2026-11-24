using System.Data;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record StockRegisterRow(BinStockLayer Layer, Guid OriginPlacementId, LocationRow Location);
internal sealed record StockRegisterPage(string Scope, DateTime AsOf, bool Complete, long MatchingPositions,
    long MatchingUnits, Guid? NextAfter, IReadOnlyList<StockRegisterRow> Rows);

internal sealed record PositionMovement(Guid Id, Guid SourcePlacementId, Guid DestinationPlacementId,
    string SourceLocationCode, string DestinationLocationCode, int Quantity, string Reason, DateTime OccurredAt);
internal sealed record PositionMovementPage(string Scope, Guid PositionId, DateTime AsOf, bool Complete,
    Guid? NextAfter, IReadOnlyList<PositionMovement> Rows);

internal sealed class StockRegister(NpgsqlDataSource db)
{
    public async Task<StockRegisterPage> Search(string? query, string? itemType, Guid? locationId,
        Guid? positionId, Guid? after, int limit, CancellationToken ct)
    {
        var term = query?.Trim() ?? "";
        if (term.Length > 160 || term.Any(char.IsControl) || limit is < 1 or > 100 ||
            (itemType is not (null or "part" or "set" or "minifigure" or "book" or "gear" or "instruction" or "original-box" or "catalog")))
            throw new Fault(400, "invalid_stock_search", "Choose a supported item type, search term and page size.");
        Guid? exactId = Guid.TryParse(term, out var id) ? id : null;
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using (var mode = Query(connection, tx, "SET TRANSACTION READ ONLY"))
            await mode.ExecuteNonQueryAsync(ct);
        await Migration.CheckWithin(connection, tx, ct);

        var filtered = PositionRows.From + "\n" + """
            JOIN brickvault.locations loc ON loc.id=p.location_id
            WHERE (p.quantity>0 OR p.id=$2 OR p.id=$5)
              AND ($5::uuid IS NULL OR p.id=$5)
              AND ($3::text IS NULL OR c.item_type=$3)
              AND ($4::uuid IS NULL OR loc.id=$4)
              AND ($1='' OR p.id=$2 OR p.delivery_id=$2 OR p.purchase_id=$2 OR p.catalog_id=$2
                   OR strpos(lower(c.item_number),lower($1))>0
                   OR strpos(lower(c.name),lower($1))>0 OR strpos(lower(c.item_type),lower($1))>0
                   OR strpos(lower(loc.code),lower($1))>0 OR strpos(lower(buy.reference),lower($1))>0
                   OR strpos(lower(d.reference),lower($1))>0)
            """;
        object[] Filters() => [term, Nullable(NpgsqlDbType.Uuid, exactId), Nullable(NpgsqlDbType.Text, itemType),
            Nullable(NpgsqlDbType.Uuid, locationId), Nullable(NpgsqlDbType.Uuid, positionId)];
        var totals = await Rows(connection, tx, "SELECT transaction_timestamp(),count(*),coalesce(sum(p.quantity),0) " + filtered,
            r => (AsOf: r.GetDateTime(0), Count: r.GetInt64(1), Units: r.GetInt64(2)), ct, Filters());
        var rows = await Rows(connection, tx, "SELECT " + PositionRows.Select +
            ",p.origin_placement_id,loc.id,loc.revision,loc.code,loc.name " + filtered +
            " AND ($6::uuid IS NULL OR p.id>$6) ORDER BY p.id LIMIT $7",
            r => new StockRegisterRow(PositionRows.ReadLayer(r), r.GetGuid(32),
                new LocationRow(r.GetGuid(33), r.GetInt32(34), r.GetString(35), r.GetString(36))),
            ct, [.. Filters(), Nullable(NpgsqlDbType.Uuid, after), limit + 1]);
        Guid? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = rows[^1].Layer.PlacementId;
        }
        var total = totals.Single();
        return new StockRegisterPage("inventory-positions", total.AsOf, true, total.Count, total.Units, next, rows);
    }

    public async Task<PositionMovementPage> History(Guid positionId, Guid? after, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100)
            throw new Fault(400, "invalid_movement_page", "Choose a movement page size between 1 and 100.");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = Query(connection, tx, "SET TRANSACTION READ ONLY"))
            await readOnly.ExecuteNonQueryAsync(ct);
        await Migration.CheckWithin(connection, tx, ct);

        await using var position = Query(connection, tx,
            "SELECT transaction_timestamp() FROM brickvault.placement_positions WHERE id=$1", positionId);
        if (await position.ExecuteScalarAsync(ct) is not DateTime asOf)
            throw new Fault(404, "unknown_position", "No stock position has that identifier.");
        DateTime? cursorTime = null;
        if (after.HasValue)
        {
            await using var cursor = Query(connection, tx, """
                SELECT a.occurred_at FROM brickvault.placement_transfers t
                JOIN brickvault.audit a ON a.command_id=t.command_id
                WHERE t.id=$1 AND (t.source_position_id=$2 OR t.id=$2)
                """, after.Value, positionId);
            cursorTime = await cursor.ExecuteScalarAsync(ct) as DateTime?
                ?? throw new Fault(400, "invalid_movement_cursor", "Select a movement from this position before loading older entries.");
        }

        var rows = await Rows(connection, tx, """
            SELECT t.id,t.source_position_id,t.source_location_code,t.destination_location_code,t.quantity,t.reason,a.occurred_at
            FROM brickvault.placement_transfers t
            JOIN brickvault.audit a ON a.command_id=t.command_id
            WHERE (t.source_position_id=$1 OR t.id=$1)
              AND ($2::timestamptz IS NULL OR (a.occurred_at,t.id)<($2,$3))
            ORDER BY a.occurred_at DESC,t.id DESC LIMIT $4
            """, r => new PositionMovement(r.GetGuid(0), r.GetGuid(1), r.GetGuid(0), r.GetString(2),
                r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetDateTime(6)),
            ct, positionId, Nullable(NpgsqlDbType.TimestampTz, cursorTime), Nullable(NpgsqlDbType.Uuid, after), limit + 1);
        Guid? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = rows[^1].Id;
        }
        return new PositionMovementPage("position-movements", positionId, asOf, true, next, rows);
    }
}
