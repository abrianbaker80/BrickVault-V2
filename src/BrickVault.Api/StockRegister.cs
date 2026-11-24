using System.Data;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record StockRegisterRow(BinStockLayer Layer, Guid OriginPlacementId, LocationRow Location);
internal sealed record StockRegisterPage(string Scope, DateTime AsOf, bool Complete, long MatchingPositions,
    long MatchingUnits, Guid? NextAfter, IReadOnlyList<StockRegisterRow> Rows);

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
            r => new StockRegisterRow(PositionRows.ReadLayer(r), r.GetGuid(31),
                new LocationRow(r.GetGuid(32), r.GetInt32(33), r.GetString(34), r.GetString(35))),
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
}
