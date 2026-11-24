using System.Data;
using Npgsql;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class BinStock(NpgsqlDataSource db)
{
    public async Task<BinStockSnapshot> Snapshot(Guid locationId, CancellationToken token)
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
                d.quantity,d.item_cost_minor,buy.currency,p.catalog_id,p.catalog_revision,p.location_code,p.quantity,
                COALESCE((SELECT sum(b.quantity) FROM brickvault.offer_backing b WHERE b.placement_id=p.id),0)::integer,
                c.id,c.revision,c.item_type,c.item_number,c.variant,c.name,c.color_id,c.color_state,c.condition,
                c.completeness,c.source,c.license,c.mapping,c.integration_eligible
            FROM brickvault.placements p
            JOIN brickvault.purchases buy ON buy.id=p.purchase_id
            JOIN brickvault.deliveries d ON d.id=p.delivery_id
            JOIN brickvault.commands j ON j.command_id=d.command_id
            JOIN brickvault.catalog c ON c.id=p.catalog_id
            WHERE p.location_id=$1
            ORDER BY p.catalog_id,p.catalog_revision,buy.reference,d.reference,p.id
            """, r =>
        {
            var recordedRevision = r.GetInt32(11);
            var quantity = r.GetInt32(13);
            var backed = r.GetInt32(14);
            var unbacked = quantity - backed;
            var current = References.ReadCatalog(r, 15);
            return new BinStockLayer(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4),
                r.GetGuid(5), r.GetGuid(6), r.GetInt32(7), Optional<int>(r, 8), r.GetString(9), r.GetGuid(10),
                recordedRevision, current, r.GetString(12), quantity, backed, unbacked,
                current.IntegrationEligible && current.Revision == recordedRevision ? unbacked : 0);
        }, token, locationId);
        return new BinStockSnapshot("bin-stock", asOf, true, location, layers.Sum(r => (long)r.Quantity),
            layers.Sum(r => (long)r.BackedQuantity), layers.Sum(r => (long)r.UnbackedQuantity),
            layers.Sum(r => (long)r.FreeToBackQuantity), layers);
    }
}
