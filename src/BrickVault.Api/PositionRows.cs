using Npgsql;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal static class PositionRows
{
    internal const string Select = """
            p.id,p.purchase_id,buy.reference,p.delivery_id,d.reference,d.command_id,j.correlation_id,
                d.quantity,d.item_cost_minor,buy.currency,p.catalog_id,p.catalog_revision,origin.location_code,p.quantity,
                COALESCE((SELECT sum(b.quantity) FROM brickvault.current_offer_backing b WHERE b.placement_id=p.id),0)::integer,
                c.id,c.revision,c.item_type,c.item_number,c.variant,c.name,c.color_id,c.color_state,c.condition,
                c.completeness,c.source,c.license,c.mapping,c.integration_eligible,
                buy.revision,p.location_code,
                EXISTS(SELECT 1 FROM brickvault.stock_shortage_cases sc WHERE sc.position_id=p.id AND sc.resolved_by_command_id IS NULL),
                COALESCE((SELECT sum(h.remaining_quantity) FROM brickvault.current_build_holds h WHERE h.position_id=p.id),0)::integer,
                COALESCE((SELECT q.quantity FROM brickvault.current_position_quarantine q WHERE q.position_id=p.id),0)
            """;
    internal const string From = """
            FROM brickvault.current_placement_stock p
            JOIN brickvault.placements origin ON origin.id=p.origin_placement_id
            JOIN brickvault.purchases buy ON buy.id=p.purchase_id
            JOIN brickvault.deliveries d ON d.id=p.delivery_id
            JOIN brickvault.commands j ON j.command_id=d.command_id
            JOIN brickvault.catalog c ON c.id=p.catalog_id
            LEFT JOIN brickvault.position_set_contents contents ON contents.position_id=p.id
            """;

    internal const string SetContentsColumn = ",contents.set_contents AS physical_set_contents";
    internal const string RegisterSelect = Select + ",p.origin_placement_id,loc.id,loc.revision,loc.code,loc.name" + SetContentsColumn;
    internal const string RegisterFrom = From + "\nJOIN brickvault.locations loc ON loc.id=p.location_id";

    internal static StockRegisterRow ReadRegister(NpgsqlDataReader r) =>
        ReadRegister(r, false);

    internal static StockRegisterRow ReadRegister(NpgsqlDataReader r, bool quarantine) =>
        new(ReadLayer(r, quarantine), r.GetGuid(34),
            new LocationRow(r.GetGuid(35), r.GetInt32(36), r.GetString(37), r.GetString(38)));

    internal static BinStockLayer ReadLayer(NpgsqlDataReader r, bool quarantine = false)
    {
        var recordedRevision = r.GetInt32(11);
        var quantity = r.GetInt32(13);
        var backed = r.GetInt32(14);
        var unbacked = quantity - backed;
        var current = References.ReadCatalog(r, 15);
        var unavailable = r.GetInt32(33);
        var contents = SetContents.Read(r,r.GetOrdinal("physical_set_contents"));
        return new BinStockLayer(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4),
            r.GetGuid(5), r.GetGuid(6), r.GetInt32(7), Optional<int>(r, 8), r.GetString(9), r.GetGuid(10),
            recordedRevision, current, r.GetString(12), quantity, backed, unbacked,
            current.IntegrationEligible && current.Revision == recordedRevision && !r.GetBoolean(31) &&
                (current.ItemType != "set" || contents is not null)
                ? unbacked - r.GetInt32(32) - unavailable : 0, quarantine ? unavailable : null, contents);
    }
}
