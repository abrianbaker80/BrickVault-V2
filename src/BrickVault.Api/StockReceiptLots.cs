using System.Data;
using System.Globalization;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record StockReceiptLotEvent(Guid PositionId, int TransferBackedQuantity, StockPositionTimelineEvent Event);

internal sealed record StockReceiptLot(string Scope, DateTime AsOf, bool Complete,
    Guid DeliveryId, string DeliveryReference, Guid PurchaseId, string PurchaseReference, int PurchaseRevision,
    string Source, string Currency, Guid? ReceiptCatalogId, int? ReceiptCatalogRevision,
    Guid ReceiptCommandId, Guid ReceiptCorrelationId, int ReceivedRevision, DateTime? ReceivedAt,
    int? ItemCostMinor, int? AllocatedLostItemCostMinor, int? RemainingItemCostMinor,
    int ReceivedQuantity, int UnplacedQuantity, int PositionedQuantity, int CurrentOnHandQuantity, int LostQuantity,
    int PositionCount, int OfferBackedQuantity, int HeldQuantity, int FreeToBackQuantity, bool HasUndatedEvents,
    IReadOnlyList<StockRegisterRow> Positions, IReadOnlyList<StockReceiptLotEvent> Events,
    string? NextPositionAfter, string? NextEventAfter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? QuarantinedQuantity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeliveryRecoveryTotals? RecoveryTotals = null);

internal sealed class StockReceiptLots(NpgsqlDataSource db)
{
    public async Task<StockReceiptLot> Read(Guid deliveryId, string? positionAfter, string? eventAfter, int positionLimit,
        int eventLimit, CancellationToken token, bool withdrawals = false, bool quarantine = false, bool closures = false,
        bool recoveries = false)
    {
        if (positionLimit is < 1 or > 100 || eventLimit is < 1 or > 100)
            throw new Fault(400, "invalid_lot_page", "Choose page sizes from 1 to 100.");
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var mode = Query(connection, tx, "SET TRANSACTION READ ONLY"))
            await mode.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection, tx, token);

        var evidence = (await Rows(connection, tx, """
            SELECT transaction_timestamp(),d.reference,d.purchase_id,buy.reference,buy.revision,buy.source,buy.currency,
                d.catalog_id,d.catalog_revision,d.command_id,c.correlation_id,c.revision,a.occurred_at,d.item_cost_minor,
                d.quantity,s.active_lost_quantity::integer,s.active_allocated_item_cost_minor::integer,
                d.quantity-COALESCE((SELECT sum(placed.quantity) FROM brickvault.placements placed WHERE placed.delivery_id=d.id),0)::integer,
                s.gross_lost_quantity::bigint,s.recovered_quantity::bigint,
                s.gross_allocated_item_cost_minor::text,s.restored_item_cost_minor::text
            FROM brickvault.deliveries d JOIN brickvault.purchases buy ON buy.id=d.purchase_id
            JOIN brickvault.commands c ON c.command_id=d.command_id
            LEFT JOIN brickvault.audit a ON a.command_id=d.command_id
            JOIN brickvault.delivery_loss_balances s ON s.delivery_id=d.id WHERE d.id=$1
            """, r => new
            {
                AsOf = r.GetDateTime(0), DeliveryReference = r.GetString(1), PurchaseId = r.GetGuid(2),
                PurchaseReference = r.GetString(3), Revision = r.GetInt32(4), Source = r.GetString(5), Currency = r.GetString(6),
                CatalogId = Optional<Guid>(r, 7), CatalogRevision = Optional<int>(r, 8), Command = r.GetGuid(9),
                Correlation = r.GetGuid(10), ReceivedRevision = r.GetInt32(11), ReceivedAt = Optional<DateTime>(r, 12),
                Cost = Optional<int>(r, 13), Received = r.GetInt32(14), Lost = r.GetInt32(15), LostBasis = Optional<int>(r, 16),
                Unplaced = r.GetInt32(17), Recovery = DeliveryRecoveryTotals.Read(r, 18, recoveries)
            }, token, deliveryId)).SingleOrDefault() ??
                throw new Fault(404, "unknown_receipt_lot", "No recorded delivery has that identifier.");
        var prefix = FormattableString.Invariant($"{deliveryId:N}.{evidence.Revision}.");
        var positionCursor = ScopedCursor(positionAfter, deliveryId, evidence.Revision, 3);
        var eventCursor = ScopedCursor(eventAfter, deliveryId, evidence.Revision, 5);
        Guid? previousPosition = null;
        if (positionCursor is not null)
        {
            if (!Guid.TryParseExact(positionCursor[2], "N", out var anchor)) throw InvalidCursor();
            await using var lookup = Query(connection, tx, """
                SELECT 1 FROM brickvault.current_placement_stock WHERE delivery_id=$1 AND id=$2
                """, deliveryId, anchor);
            if (await lookup.ExecuteScalarAsync(token) is null) throw InvalidCursor();
            previousPosition = anchor;
        }
        (int Revision, string Kind, Guid EventId, Guid PositionId)? previousEvent = null;
        if (eventCursor is not null)
        {
            var kind = eventCursor[2];
            if (!Guid.TryParseExact(eventCursor[3], "N", out var id) ||
                !Guid.TryParseExact(eventCursor[4], "N", out var position)) throw InvalidCursor();
            await using var lookup = Query(connection, tx, StockPositionTimelines.ScopedEvents(true) + "\n" + """
                SELECT purchase_revision FROM events WHERE kind=$2 AND id=$3 AND position_id=$4
                """, deliveryId, kind, id, position);
            if (await lookup.ExecuteScalarAsync(token) is not int revision) throw InvalidCursor();
            previousEvent = (revision, kind, id, position);
        }

        var totals = (await Rows(connection, tx, """
            WITH stock AS (SELECT * FROM brickvault.current_placement_stock WHERE delivery_id=$1),
            backing AS (
                SELECT b.placement_id,sum(b.quantity)::integer AS quantity
                FROM brickvault.current_offer_backing b JOIN stock s ON s.id=b.placement_id GROUP BY b.placement_id
            ), held AS (
                SELECT h.position_id,sum(h.remaining_quantity)::integer AS quantity
                FROM brickvault.current_build_holds h JOIN stock s ON s.id=h.position_id GROUP BY h.position_id
            )
            SELECT count(*)::integer, COALESCE(sum(s.quantity),0)::integer, COALESCE(sum(b.quantity),0)::integer,
                COALESCE(sum(h.quantity),0)::integer,
                COALESCE(sum(CASE WHEN c.integration_eligible AND c.revision=s.catalog_revision AND sc.id IS NULL
                    THEN s.quantity-COALESCE(b.quantity,0)-COALESCE(h.quantity,0)-COALESCE(q.quantity,0) ELSE 0 END),0)::integer,
                COALESCE(sum(q.quantity),0)::integer
            FROM stock s JOIN brickvault.catalog c ON c.id=s.catalog_id
            LEFT JOIN brickvault.stock_shortage_cases sc ON sc.position_id=s.id AND sc.resolved_by_command_id IS NULL
            LEFT JOIN backing b ON b.placement_id=s.id LEFT JOIN held h ON h.position_id=s.id
            LEFT JOIN brickvault.current_position_quarantine q ON q.position_id=s.id
            """, r => (Positions: r.GetInt32(0), Positioned: r.GetInt32(1), Backed: r.GetInt32(2),
                Held: r.GetInt32(3), Free: r.GetInt32(4), Quarantined: r.GetInt32(5)), token, deliveryId)).Single();
        var positions = await Rows(connection, tx, "SELECT " + PositionRows.RegisterSelect + "\n" +
            PositionRows.RegisterFrom + "\nWHERE p.delivery_id=$1 AND ($2::uuid IS NULL OR p.id>$2) ORDER BY p.id LIMIT $3",
            r => PositionRows.ReadRegister(r, quarantine), token, deliveryId,
            Nullable(NpgsqlDbType.Uuid, previousPosition), positionLimit + 1);
        var events = await StockPositionTimelines.ReadEvents(connection, tx, deliveryId, true, previousEvent?.Revision, previousEvent?.Kind,
            previousEvent?.EventId, previousEvent?.PositionId, eventLimit + 1, token);

        string? nextPosition = null;
        if (positions.Count > positionLimit)
        {
            positions.RemoveAt(positionLimit);
            nextPosition = $"{prefix}{positions[^1].Layer.PlacementId:N}";
        }
        string? nextEvent = null;
        if (events.Count > eventLimit)
        {
            events.RemoveAt(eventLimit);
            var last = events[^1];
            nextEvent = $"{prefix}{last.Event.Kind}.{last.Event.Id:N}.{last.PositionId:N}";
        }
        var history = (await Rows(connection, tx, StockPositionTimelines.ScopedEvents(true) + "\n" + """
            SELECT EXISTS (SELECT 1 FROM events WHERE occurred_at IS NULL),
                EXISTS (SELECT 1 FROM events WHERE kind IN ('quarantine','quarantine-release')),
                EXISTS (SELECT 1 FROM events WHERE kind='offer-withdrawal'),
                EXISTS (SELECT 1 FROM events WHERE kind='shortage-closure')
            """, r => (Undated: r.GetBoolean(0), HasQuarantine: r.GetBoolean(1), HasWithdrawals: r.GetBoolean(2), HasClosures: r.GetBoolean(3)), token, deliveryId)).Single();
        if (!quarantine && history.HasQuarantine) throw StockQuarantines.Upgrade();
        if (!withdrawals && history.HasWithdrawals) throw DraftBackingWithdrawals.Upgrade();
        if (!closures && history.HasClosures) throw StockShortageClosures.Upgrade();
        var hasUndated = evidence.ReceivedAt is null || history.Undated;
        var onHand = evidence.Received - evidence.Lost;
        if (onHand != evidence.Unplaced + totals.Positioned)
            throw new Fault(503, "receipt_lot_inconsistent", "The recorded receipt and projected positions could not be reconciled.");
        return new StockReceiptLot("receipt-lot", evidence.AsOf, true, deliveryId, evidence.DeliveryReference,
            evidence.PurchaseId, evidence.PurchaseReference, evidence.Revision, evidence.Source, evidence.Currency,
            evidence.CatalogId, evidence.CatalogRevision, evidence.Command, evidence.Correlation, evidence.ReceivedRevision,
            evidence.ReceivedAt, evidence.Cost, evidence.LostBasis, evidence.Cost - evidence.LostBasis,
            evidence.Received, evidence.Unplaced, totals.Positioned, onHand, evidence.Lost, totals.Positions, totals.Backed,
            totals.Held, totals.Free, hasUndated, positions,
            events.Select(row => row.Event.Kind == "transfer-out"
                ? new StockReceiptLotEvent(row.PositionId, -row.Event.OfferBackedDelta,
                    row.Event with { PhysicalDelta = 0, OfferBackedDelta = 0 })
                : new StockReceiptLotEvent(row.PositionId, 0, row.Event)).ToList(), nextPosition, nextEvent,
            quarantine ? totals.Quarantined : null, evidence.Recovery);
    }

    private static string[]? ScopedCursor(string? cursor, Guid deliveryId, int revision, int size)
    {
        if (cursor is null) return null;
        if (cursor.Length > 129) throw InvalidCursor();
        var parts = cursor.Split('.');
        if (parts.Length != size || !Guid.TryParseExact(parts[0], "N", out var scope) || scope != deliveryId ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var cursorRevision) ||
            cursorRevision < 1) throw InvalidCursor();
        if (cursorRevision != revision)
            throw new Fault(409, "stale_lot_page", "The purchase changed. Reload the lot from the first page.");
        return parts;
    }

    private static Fault InvalidCursor() =>
        new(400, "invalid_lot_cursor", "Use the continuation from this receipt lot and the correct list.");
}
