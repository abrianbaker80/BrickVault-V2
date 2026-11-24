using System.Data;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class StockAdjustmentReviews(NpgsqlDataSource db)
{
    public async Task<StockAdjustmentReview> Read(Guid locationId, Guid positionId, Guid caseId, string? after,
        int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100)
            throw new Fault(400, "invalid_adjustment_page", "Choose an offer page size from 1 to 100.");
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var mode = Query(connection, tx, "SET TRANSACTION READ ONLY"))
            await mode.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection, tx, token);

        var rows = await Rows(connection, tx, """
            SELECT transaction_timestamp(),l.id,l.revision,l.code,l.name,p.purchase_id,buy.revision,p.quantity,
                d.id,d.reference,d.quantity,d.item_cost_minor,buy.currency,p.origin_placement_id,p.catalog_id,p.catalog_revision,
                sc.id, COALESCE((SELECT sum(h.remaining_quantity) FROM brickvault.current_build_holds h
                    WHERE h.position_id=p.id),0)::integer,
                offers.backed,offers.entries
            FROM brickvault.current_placement_stock p
            JOIN brickvault.locations l ON l.id=p.location_id
            JOIN brickvault.purchases buy ON buy.id=p.purchase_id
            JOIN brickvault.deliveries d ON d.id=p.delivery_id
            LEFT JOIN brickvault.stock_shortage_cases sc
                ON sc.position_id=p.id AND sc.id=$3 AND sc.resolved_by_command_id IS NULL
            CROSS JOIN LATERAL (
                SELECT COALESCE(sum(b.quantity),0)::integer AS backed,count(*)::integer AS entries
                FROM brickvault.current_offer_backing b WHERE b.placement_id=p.id
            ) offers
            WHERE p.id=$1 AND p.location_id=$2
            """, r => (AsOf: r.GetDateTime(0), Location: new LocationRow(r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4)),
                Purchase: r.GetGuid(5), Revision: r.GetInt32(6), Ledger: r.GetInt32(7),
                Receipt: new AdjustmentReceiptEvidence(r.GetGuid(8), r.GetString(9), r.GetInt32(10), Optional<int>(r, 11),
                    r.GetString(12), r.GetGuid(13), r.GetGuid(14), r.GetInt32(15)),
                Case: Optional<Guid>(r, 16), Protected: r.GetInt32(17), Backed: r.GetInt32(18), OfferCount: r.GetInt32(19)),
            token, positionId, locationId, caseId);
        if (rows.Count == 0)
            throw new Fault(404, "unknown_adjustment_position", "No receipt position with that identifier belongs to this bin.");
        var position = rows.Single();
        if (position.Case is null)
            throw new Fault(404, "unknown_open_shortage", "No open shortage with that identifier belongs to this receipt position.");

        var latest = (await Rows(connection, tx, StockCounts.ObservationProjection + "\n" + """
            WHERE n.position_id=$1
            ORDER BY n.expected_purchase_revision DESC LIMIT 1
            """, StockCounts.ReadObservation, token, positionId)).Single();

        var cursor = await CheckCursor(connection, tx, caseId, positionId, position.Revision, after, token);
        var offers = await Rows(connection, tx, """
            SELECT o.id,b.quantity,o.unit_price_minor
            FROM brickvault.current_offer_backing b JOIN brickvault.offers o ON o.id=b.offer_id
            WHERE b.placement_id=$1 AND ($2::uuid IS NULL OR o.id>$2)
            ORDER BY o.id LIMIT $3
            """, r => new AdjustmentOfferBacking(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), position.Receipt.Currency),
            token, positionId, Nullable(NpgsqlDbType.Uuid, cursor), limit + 1);
        string? next = null;
        if (offers.Count > limit)
        {
            offers.RemoveAt(limit);
            next = FormattableString.Invariant($"v1.{caseId:N}.{positionId:N}.{position.Revision}.{offers[^1].Id:N}");
        }

        var loss = Math.Max(0, position.Ledger - latest.CountedQuantity);
        var afterLoss = position.Ledger - loss;
        var capacity = Math.Max(0, afterLoss - position.Protected);
        return new StockAdjustmentReview("stock-adjustment-review", position.AsOf, true, position.Location,
            positionId, caseId, position.Purchase, position.Revision, position.Receipt, latest, position.Ledger,
            loss, afterLoss, position.Protected, Math.Max(0, position.Protected - afterLoss), position.Backed,
            position.OfferCount, capacity, Math.Max(0, position.Backed - capacity), offers, next);
    }

    private static async Task<Guid?> CheckCursor(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid caseId, Guid positionId, int revision, string? after, CancellationToken token)
    {
        if (after is null) return null;
        if (after.Length > 112) throw InvalidCursor();
        var parts = after.Split('.');
        if (parts.Length != 5 || parts[0] != "v1" ||
            !Guid.TryParseExact(parts[1], "N", out var cursorCase) || cursorCase != caseId ||
            !Guid.TryParseExact(parts[2], "N", out var cursorPosition) || cursorPosition != positionId ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var cursorRevision) ||
            !Guid.TryParseExact(parts[4], "N", out var offerId))
            throw InvalidCursor();
        if (cursorRevision != revision)
            throw new Fault(409, "stale_adjustment_page", "This purchase has changed. Reload the review from its first offer page.");
        await using var membership = Query(connection, tx, """
            SELECT 1 FROM brickvault.current_offer_backing WHERE placement_id=$1 AND offer_id=$2
            """, positionId, offerId);
        if (await membership.ExecuteScalarAsync(token) is null) throw InvalidCursor();
        return offerId;
    }

    private static Fault InvalidCursor() =>
        new(400, "invalid_adjustment_cursor", "Use the next cursor from this receipt position and open shortage.");
}
