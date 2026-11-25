using System.Data;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record StockPositionTimelineEvent(string Kind, Guid Id, Guid CommandId, Guid CorrelationId,
    int PurchaseRevision, DateTime? OccurredAt, int Quantity, int PhysicalDelta, int ReservedDelta, int OfferBackedDelta, Guid LocationId,
    string? LocationCodeAtEvent, string? Reason, Guid? RelatedPositionId, Guid? RelatedLocationId,
    string? RelatedLocationCode, Guid? ObservationId, Guid? CaseId, Guid? PostingId, Guid? HoldId, string? HoldPurpose,
    Guid? OfferId, int? LedgerQuantityAtWitness);
internal sealed record StockPositionTimeline(string Scope, DateTime AsOf, bool Complete, bool HasUndatedEvents, Guid PositionId,
    Guid PurchaseId, Guid OriginPlacementId, Guid DeliveryId, LocationRow Location,
    string? NextAfter, IReadOnlyList<StockPositionTimelineEvent> Events, StockRegisterRow Position);

internal sealed record StockTimelineAtPosition(Guid PositionId, StockPositionTimelineEvent Event);

internal sealed class StockPositionTimelines(NpgsqlDataSource db)
{
    // The source branches select historical records at a single position, including depleted positions.
    // Only a movement introduces stock into a transfer-created position.
    internal static string ScopedEvents(bool lot) => $$"""
        WITH scope_positions AS ({{(lot
            ? "SELECT p.id FROM brickvault.placement_positions p JOIN brickvault.placements o ON o.id=p.origin_placement_id WHERE o.delivery_id=$1"
            : "SELECT id FROM brickvault.placement_positions WHERE id=$1")}}),
        facts(position_id,kind,id,command_id,evidence_time,quantity,physical_delta,reserved_delta,initial_backing,
            location_id,location_code,reason,related_position_id,related_location_id,related_location_code,
            observation_id,case_id,posting_id,hold_id,hold_purpose,offer_id,ledger_at_witness) AS (
            SELECT p.id,'putaway',p.id,p.command_id,NULL::timestamptz,p.quantity,p.quantity,0,0,
                p.location_id,p.location_code,NULL::text,NULL::uuid,NULL::uuid,NULL::text,
                NULL::uuid,NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,NULL::uuid,NULL::integer
            FROM brickvault.placements p JOIN scope_positions s ON s.id=p.id

            UNION ALL
            SELECT s.id,CASE WHEN t.id=s.id THEN 'transfer-in' ELSE 'transfer-out' END,t.id,t.command_id,NULL,
                t.quantity,CASE WHEN t.id=s.id THEN t.quantity ELSE -t.quantity END,0,0,
                CASE WHEN t.id=s.id THEN t.destination_location_id ELSE t.source_location_id END,
                CASE WHEN t.id=s.id THEN t.destination_location_code ELSE t.source_location_code END,t.reason,
                CASE WHEN t.id=s.id THEN t.source_position_id ELSE t.id END,
                CASE WHEN t.id=s.id THEN t.source_location_id ELSE t.destination_location_id END,
                CASE WHEN t.id=s.id THEN t.source_location_code ELSE t.destination_location_code END,
                NULL,NULL,NULL,NULL,NULL,NULL,NULL
            FROM brickvault.placement_transfers t JOIN scope_positions s
                ON t.source_position_id=s.id {{(lot ? "" : "OR t.id=s.id")}}

            UNION ALL
            SELECT n.position_id,'count',n.id,n.command_id,n.observed_at,n.counted_quantity,0,0,0,
                n.location_id,n.location_code,n.reason,NULL,NULL,NULL,n.id,NULL,NULL,NULL,NULL,NULL,n.ledger_quantity
            FROM brickvault.stock_count_observations n
            JOIN scope_positions s ON s.id=n.position_id

            UNION ALL
            SELECT h.position_id,'hold',h.id,h.command_id,NULL,h.quantity,0,h.quantity,0,
                h.location_id,h.location_code,h.reason,NULL,NULL,NULL,NULL,NULL,NULL,h.id,h.purpose,NULL,NULL
            FROM brickvault.stock_build_holds h JOIN scope_positions s ON s.id=h.position_id

            UNION ALL
            SELECT h.position_id,'hold-release',r.id,r.command_id,NULL,r.quantity,0,-r.quantity,0,
                h.location_id,NULL,r.reason,NULL,NULL,NULL,r.expected_observation_id,r.shortage_id,NULL,h.id,h.purpose,NULL,NULL
            FROM brickvault.stock_build_holds h JOIN brickvault.stock_build_hold_releases r ON r.hold_id=h.id
            JOIN scope_positions s ON s.id=h.position_id

            UNION ALL
            SELECT l.position_id,'stock-loss',l.id,l.command_id,NULL,l.lost_quantity,-l.lost_quantity,0,0,
                l.location_id,l.location_code,l.reason,NULL,NULL,NULL,l.observation_id,l.case_id,l.id,NULL,NULL,NULL,NULL
            FROM brickvault.stock_loss_postings l JOIN scope_positions s ON s.id=l.position_id

            UNION ALL
            SELECT b.placement_id,'offer-backing',o.id,o.command_id,NULL,b.quantity,0,0,b.quantity,
                p.location_id,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,o.id,NULL
            FROM brickvault.offer_backing b JOIN brickvault.offers o ON o.id=b.offer_id
            JOIN brickvault.placement_positions p ON p.id=b.placement_id
            JOIN scope_positions s ON s.id=b.placement_id
        ), events AS (
            SELECT f.*,c.revision AS purchase_revision,c.correlation_id,COALESCE(f.evidence_time,a.occurred_at) AS occurred_at
            FROM facts f JOIN brickvault.commands c ON c.command_id=f.command_id
            LEFT JOIN brickvault.audit a ON a.command_id=f.command_id
        )
        """;

    public async Task<StockPositionTimeline> Read(Guid positionId, string? after, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100)
            throw new Fault(400, "invalid_timeline_page", "Choose a timeline page size between 1 and 100.");
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var readOnly = Query(connection, tx, "SET TRANSACTION READ ONLY"))
            await readOnly.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection, tx, token);

        var positions = await Rows(connection, tx, "SELECT " + PositionRows.RegisterSelect + "\n" +
            PositionRows.RegisterFrom + "\nWHERE p.id=$1", PositionRows.ReadRegister, token, positionId);
        var position = positions.SingleOrDefault() ??
            throw new Fault(404, "unknown_position", "No stock position has that identifier.");
        var metadata = (await Rows(connection, tx, ScopedEvents(false) + "\n" + """
            SELECT transaction_timestamp(),EXISTS (SELECT 1 FROM events WHERE occurred_at IS NULL)
            """, r => (AsOf: r.GetDateTime(0), HasUndatedEvents: r.GetBoolean(1)), token, positionId)).Single();

        var cursor = await Cursor(connection, tx, positionId, after, token);
        var rows = await ReadEvents(connection, tx, positionId, false, cursor?.Revision, cursor?.Kind, cursor?.Id,
            cursor?.Id is null ? null : positionId, limit + 1, token);
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = $"{positionId:N}.{rows[^1].Event.Kind}.{rows[^1].Event.Id:N}";
        }
        return new StockPositionTimeline("position-timeline", metadata.AsOf, true, metadata.HasUndatedEvents, positionId,
            position.Layer.PurchaseId, position.OriginPlacementId, position.Layer.DeliveryId, position.Location, next, rows.Select(r => r.Event).ToList(), position);
    }

    internal static Task<List<StockTimelineAtPosition>> ReadEvents(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid scope, bool lot, int? revision, string? kind, Guid? eventId, Guid? positionId, int limit, CancellationToken token) =>
        Rows(connection, tx, ScopedEvents(lot) + "\n" + """
            SELECT e.kind,e.id,e.command_id,e.correlation_id,e.occurred_at,e.quantity,e.physical_delta,e.reserved_delta,
                CASE WHEN e.kind IN ('transfer-in','transfer-out') THEN
                    COALESCE((SELECT sum(b.quantity) FROM brickvault.placement_transfer_backing b
                              WHERE b.transfer_id=e.id),0)::integer * sign(e.physical_delta)::integer
                    WHEN e.kind='stock-loss' THEN
                    -COALESCE((SELECT sum(r.quantity) FROM brickvault.stock_loss_offer_reductions r WHERE r.loss_id=e.id),0)::integer
                    ELSE e.initial_backing END,
                e.location_id,e.location_code,e.reason,e.related_position_id,e.related_location_id,e.related_location_code,
                e.observation_id,
                CASE WHEN e.kind='count' THEN (
                    SELECT s.id FROM brickvault.stock_shortage_cases s
                    JOIN brickvault.stock_count_observations opening ON opening.id=s.id
                    LEFT JOIN brickvault.commands closing ON closing.command_id=s.resolved_by_command_id
                    WHERE s.position_id=e.position_id AND opening.expected_purchase_revision<e.purchase_revision
                        AND (s.resolved_by_command_id IS NULL OR e.purchase_revision<closing.revision)
                    ORDER BY opening.expected_purchase_revision DESC LIMIT 1
                ) ELSE e.case_id END,
                e.posting_id,e.hold_id,e.hold_purpose,e.offer_id,e.ledger_at_witness,e.purchase_revision,e.position_id
            FROM (
                SELECT * FROM events
                WHERE $2::integer IS NULL OR (purchase_revision,kind COLLATE "C",id,position_id)<($2,$3::text COLLATE "C",$4::uuid,$5::uuid)
                ORDER BY purchase_revision DESC,kind COLLATE "C" DESC,id DESC,position_id DESC LIMIT $6
            ) e ORDER BY e.purchase_revision DESC,e.kind COLLATE "C" DESC,e.id DESC,e.position_id DESC
            """, r => new StockTimelineAtPosition(r.GetGuid(23), new StockPositionTimelineEvent(r.GetString(0),r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),r.GetInt32(22),Optional<DateTime>(r,4),
                r.GetInt32(5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),r.GetGuid(9),Text(r,10),Text(r,11),
                Optional<Guid>(r,12),Optional<Guid>(r,13),Text(r,14),Optional<Guid>(r,15),Optional<Guid>(r,16),
                Optional<Guid>(r,17),Optional<Guid>(r,18),Text(r,19),Optional<Guid>(r,20),Optional<int>(r,21))),
            token, scope, Nullable(NpgsqlDbType.Integer,revision), Nullable(NpgsqlDbType.Text,kind),
            Nullable(NpgsqlDbType.Uuid,eventId), Nullable(NpgsqlDbType.Uuid,positionId), limit);

    private static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task<(int Revision, string Kind, Guid Id)?> Cursor(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid positionId, string? after, CancellationToken token)
    {
        if (after is null) return null;
        if (after.Length > 79) throw InvalidCursor();
        var parts = after.Split('.');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0],"N",out var scope) || scope != positionId ||
            parts[1] is not ("putaway" or "transfer-in" or "transfer-out" or "count" or "hold" or "hold-release" or
            "stock-loss" or "offer-backing") || !Guid.TryParseExact(parts[2],"N",out var id)) throw InvalidCursor();
        await using var anchor = Query(connection, tx, ScopedEvents(false) + "\n" + """
            SELECT purchase_revision FROM events WHERE kind=$2 AND id=$3
            """,positionId,parts[1],id);
        if (await anchor.ExecuteScalarAsync(token) is not int revision) throw InvalidCursor();
        return (revision,parts[1],id);
    }

    private static Fault InvalidCursor() =>
        new(400,"invalid_timeline_cursor","Use the next cursor from this stock position's timeline.");
}
