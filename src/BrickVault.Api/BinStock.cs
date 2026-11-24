using System.Data;
using Npgsql;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class BinStock(NpgsqlDataSource db)
{
    public async Task<BinStockSnapshot> Snapshot(Guid locationId, CancellationToken token)
    {
        var bin = await Read(locationId, includeMovements: false, token);
        return new BinStockSnapshot("bin-stock", bin.AsOf, bin.Complete, bin.Location,
            bin.PhysicalOnHand, bin.BackedQuantity, bin.UnbackedQuantity, bin.FreeToBackQuantity,
            bin.Positions.Select(p => p.Layer).ToList());
    }

    public Task<BinTransferSnapshot> Movements(Guid locationId, CancellationToken token) =>
        Read(locationId, includeMovements: true, token);

    private async Task<BinTransferSnapshot> Read(Guid locationId, bool includeMovements, CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var readOnly = Query(connection, tx, "SET TRANSACTION READ ONLY"))
            await readOnly.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection, tx, token);

        await using var clock = Query(connection, tx, "SELECT transaction_timestamp()");
        var asOf = (DateTime)(await clock.ExecuteScalarAsync(token))!;
        var locations = await Rows(connection, tx, """
            SELECT id,revision,code,name FROM brickvault.locations WHERE id=$1
            """, r => new LocationRow(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3)), token, locationId);
        var location = locations.SingleOrDefault() ??
            throw new Fault(404, "unknown_location", "No registered location has that identifier.");

        var layers = await Rows(connection, tx, """
            SELECT p.id,p.purchase_id,buy.reference,p.delivery_id,d.reference,d.command_id,j.correlation_id,
                d.quantity,d.item_cost_minor,buy.currency,p.catalog_id,p.catalog_revision,origin.location_code,p.quantity,
                COALESCE((SELECT sum(b.quantity) FROM brickvault.current_offer_backing b WHERE b.placement_id=p.id),0)::integer,
                c.id,c.revision,c.item_type,c.item_number,c.variant,c.name,c.color_id,c.color_state,c.condition,
                c.completeness,c.source,c.license,c.mapping,c.integration_eligible,
                buy.revision,p.location_code
            FROM brickvault.current_placement_stock p
            JOIN brickvault.placements origin ON origin.id=p.origin_placement_id
            JOIN brickvault.purchases buy ON buy.id=p.purchase_id
            JOIN brickvault.deliveries d ON d.id=p.delivery_id
            JOIN brickvault.commands j ON j.command_id=d.command_id
            JOIN brickvault.catalog c ON c.id=p.catalog_id
            WHERE p.location_id=$1 AND p.quantity > 0
            ORDER BY p.catalog_id,p.catalog_revision,buy.reference,d.reference,p.id
            """, r =>
        {
            var recordedRevision = r.GetInt32(11);
            var quantity = r.GetInt32(13);
            var backed = r.GetInt32(14);
            var unbacked = quantity - backed;
            var current = References.ReadCatalog(r, 15);
            var layer = new BinStockLayer(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4),
                r.GetGuid(5), r.GetGuid(6), r.GetInt32(7), Optional<int>(r, 8), r.GetString(9), r.GetGuid(10),
                recordedRevision, current, r.GetString(12), quantity, backed, unbacked,
                current.IntegrationEligible && current.Revision == recordedRevision ? unbacked : 0);
            return new TransferPosition(layer, r.GetInt32(29), r.GetString(30), []);
        }, token, locationId);

        List<TransferMovement> movements = [];
        if (includeMovements)
        {
            var activeBacking = (await Rows(connection, tx, """
                SELECT b.placement_id,b.offer_id,b.quantity,o.unit_price_minor,p.currency
                FROM brickvault.current_offer_backing b
                JOIN brickvault.current_placement_stock s ON s.id=b.placement_id
                JOIN brickvault.offers o ON o.id=b.offer_id
                JOIN brickvault.purchases p ON p.id=o.purchase_id
                WHERE s.location_id=$1 AND s.quantity>0 ORDER BY b.offer_id
                """, r => (Position: r.GetGuid(0), Choice: ReadChoice(r)), token, locationId))
                .ToLookup(x => x.Position, x => x.Choice);
            layers = layers.Select(p => p with { Backing = activeBacking[p.Layer.PlacementId].ToList() }).ToList();
            movements = await History(connection, tx, locationId, token);
        }

        return new BinTransferSnapshot("bin-transfers", asOf, true, location,
            layers.Sum(r => (long)r.Layer.Quantity), layers.Sum(r => (long)r.Layer.BackedQuantity),
            layers.Sum(r => (long)r.Layer.UnbackedQuantity), layers.Sum(r => (long)r.Layer.FreeToBackQuantity),
            layers, movements);
    }

    private static TransferBackingChoice ReadChoice(NpgsqlDataReader r) =>
        new(r.GetGuid(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4));

    private static async Task<List<TransferMovement>> History(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid locationId, CancellationToken token)
    {
        var backing = (await Rows(connection, tx, """
            SELECT b.transfer_id,b.offer_id,b.quantity,o.unit_price_minor,p.currency
            FROM brickvault.placement_transfer_backing b
            JOIN brickvault.placement_transfers t ON t.id=b.transfer_id
            JOIN brickvault.offers o ON o.id=b.offer_id
            JOIN brickvault.purchases p ON p.id=o.purchase_id
            WHERE t.source_location_id=$1 OR t.destination_location_id=$1
            ORDER BY b.offer_id
            """, r => (Transfer: r.GetGuid(0), Choice: ReadChoice(r)), token, locationId))
            .ToLookup(x => x.Transfer, x => x.Choice);

        return await Rows(connection, tx, """
            SELECT t.id,t.command_id,c.correlation_id,t.purchase_id,p.reference,d.id,d.reference,
                t.source_position_id,t.source_location_id,t.source_location_code,t.destination_location_id,
                t.destination_location_code,s.catalog_id,s.catalog_revision,t.quantity,t.reason,a.occurred_at
            FROM brickvault.placement_transfers t
            JOIN brickvault.commands c ON c.command_id=t.command_id
            JOIN brickvault.audit a ON a.command_id=t.command_id
            JOIN brickvault.purchases p ON p.id=t.purchase_id
            JOIN brickvault.placement_positions s ON s.id=t.source_position_id
            JOIN brickvault.placements origin ON origin.id=s.origin_placement_id
            JOIN brickvault.deliveries d ON d.id=origin.delivery_id
            WHERE t.source_location_id=$1 OR t.destination_location_id=$1
            ORDER BY a.occurred_at DESC,t.id
            """, r => new TransferMovement(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3),
            r.GetString(4), r.GetGuid(5), r.GetString(6), r.GetGuid(7), r.GetGuid(0), r.GetGuid(8),
            r.GetString(9), r.GetGuid(10), r.GetString(11), r.GetGuid(12), r.GetInt32(13), r.GetInt32(14),
            r.GetString(15), backing[r.GetGuid(0)].ToList(), r.GetDateTime(16)), token, locationId);
    }
}
