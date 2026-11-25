using System.Data;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class StockCounts(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(StockCountCommand command, Guid correlationId, CancellationToken token)
    {
        command.Validate();
        var digest = command.Digest();
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (await CommandJournal.Replay(connection, tx, command.CommandId, digest, token) is { } replay) return replay;

        try
        {
            var id = Guid.NewGuid();
            await using var row = Query(connection, tx, """
                INSERT INTO brickvault.stock_count_observations(id,command_id,purchase_id,position_id,location_id,
                    location_code,expected_purchase_revision,counted_quantity,reason)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) RETURNING expected_purchase_revision+1
                """, id, command.CommandId, command.PurchaseId, command.PositionId, command.LocationId,
                command.ConfirmedLocationCode, command.ExpectedRevision, command.CountedQuantity, command.Reason);
            var revision = (int)(await row.ExecuteScalarAsync(token))!;
            var receipt = new Receipt(command.CommandId, id, revision, "count", correlationId);
            await CommandJournal.Append(connection, tx, receipt, digest, "record-receiving", token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException ex) when (ex.ConstraintName is "count_position" or "count_bin" or "count_revision")
        {
            throw ex.ConstraintName switch
            {
                "count_revision" => StalePurchase(),
                "count_bin" => new Fault(409, "count_bin_confirmation_mismatch", "The entered bin code must identify the selected location."),
                _ => new Fault(409, "count_position_mismatch", "The selected receipt position is empty or does not belong to this purchase and bin.")
            };
        }
    }

    private static Fault StalePurchase() =>
        new(409, "revision_conflict", "This purchase has changed. Reload its current positions and confirm the count.");

    internal const string ObservationProjection = """
        SELECT n.id,n.command_id,c.correlation_id,n.purchase_id,n.position_id,n.location_id,n.location_code,
            n.expected_purchase_revision,n.ledger_quantity,n.offer_backed_quantity,n.counted_quantity,n.reason,n.observed_at
        FROM brickvault.stock_count_observations n JOIN brickvault.commands c ON c.command_id=n.command_id
        """;

    public async Task<BinCountSnapshot> Snapshot(Guid locationId, CancellationToken token, Guid? positionId = null,
        Guid? after = null, int limit = 25, Guid? observationId = null, Guid? commandId = null, bool closures = false)
    {
        var exact = observationId.HasValue || commandId.HasValue;
        if (limit is < 1 or > 100 || (observationId.HasValue && commandId.HasValue) || (exact && after.HasValue))
            throw new Fault(400, "invalid_count_page", "Choose a page size from 1 to 100 or look up one count receipt without a cursor.");

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

        Guid? next = null;
        int? witnessRevision = null;
        List<StockCountObservation> observations;
        if (exact)
        {
            observations = await Rows(connection, tx, ObservationProjection + "\n" + """
                WHERE n.location_id=$1 AND ($2::uuid IS NULL OR n.position_id=$2)
                  AND ($3::uuid IS NULL OR n.id=$3) AND ($4::uuid IS NULL OR n.command_id=$4)
                """, ReadObservation, token, locationId, Nullable(NpgsqlDbType.Uuid, positionId),
                Nullable(NpgsqlDbType.Uuid, observationId), Nullable(NpgsqlDbType.Uuid, commandId));
            var found = observations.SingleOrDefault() ??
                throw new Fault(404, "unknown_count_observation", "No matching count receipt exists in the selected scope.");
            positionId = found.PositionId;
            witnessRevision = found.ExpectedPurchaseRevision;
        }
        else
        {
            if (positionId is Guid position)
            {
                await using var lookup = Query(connection, tx, """
                    SELECT 1 FROM brickvault.placement_positions WHERE id=$1 AND location_id=$2
                    """, position, locationId);
                if (await lookup.ExecuteScalarAsync(token) is null)
                    throw new Fault(404, "unknown_count_position", "No receipt position with that identifier belongs to this bin.");
            }

            DateTime? cursor = null;
            if (after is Guid cursorId)
            {
                await using var lookup = Query(connection, tx, """
                    SELECT observed_at FROM brickvault.stock_count_observations
                    WHERE id=$1 AND location_id=$2 AND ($3::uuid IS NULL OR position_id=$3)
                    """, cursorId, locationId, Nullable(NpgsqlDbType.Uuid, positionId));
                cursor = (DateTime?)await lookup.ExecuteScalarAsync(token) ??
                    throw new Fault(400, "invalid_count_cursor", "Use the next cursor from this bin and position's count history.");
            }
            observations = await Rows(connection, tx, ObservationProjection + "\n" + """
                WHERE n.location_id=$1 AND ($2::uuid IS NULL OR n.position_id=$2)
                    AND ($3::timestamptz IS NULL OR (n.observed_at,n.id)<($3,$4))
                ORDER BY n.observed_at DESC,n.id DESC LIMIT $5
                """, ReadObservation, token, locationId, Nullable(NpgsqlDbType.Uuid, positionId),
                Nullable(NpgsqlDbType.TimestampTz, cursor), Nullable(NpgsqlDbType.Uuid, after), limit + 1);
            if (observations.Count > limit)
            {
                observations.RemoveAt(limit);
                next = observations[^1].Id;
            }
        }

        var positions = await Rows(connection, tx, """
            SELECT p.id,p.purchase_id,buy.reference,d.id,d.reference,buy.revision,p.catalog_id,p.catalog_revision,
                p.quantity,COALESCE((SELECT sum(b.quantity) FROM brickvault.current_offer_backing b
                 WHERE b.placement_id=p.id),0)::integer,
                EXISTS(SELECT 1 FROM brickvault.stock_shortage_cases c
                    WHERE c.position_id=p.id AND c.resolved_by_command_id IS NULL)
            FROM brickvault.current_placement_stock p JOIN brickvault.purchases buy ON buy.id=p.purchase_id
            JOIN brickvault.deliveries d ON d.id=p.delivery_id
            WHERE p.location_id=$1 AND p.quantity>0 AND ($2::uuid IS NULL OR p.id=$2)
            ORDER BY buy.reference,d.reference,p.id
            """, r => new CountPosition(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetGuid(3),
            r.GetString(4),r.GetInt32(5),r.GetGuid(6),r.GetInt32(7),r.GetInt32(8),r.GetInt32(9),r.GetBoolean(10)),
            token, locationId, Nullable(NpgsqlDbType.Uuid, positionId));

        var shortages = await Rows(connection, tx, """
            SELECT c.id,c.position_id,o.purchase_id,o.observed_at,o.ledger_quantity-o.counted_quantity,
                latest.id,latest.observed_at,greatest(0,latest.ledger_quantity-latest.counted_quantity)
            FROM brickvault.stock_shortage_cases c JOIN brickvault.stock_count_observations o ON o.id=c.id
            JOIN LATERAL (
                SELECT n.id,n.observed_at,n.ledger_quantity,n.counted_quantity
                FROM brickvault.stock_count_observations n WHERE n.position_id=c.position_id
                ORDER BY n.expected_purchase_revision DESC LIMIT 1
            ) latest ON true
            WHERE c.resolved_by_command_id IS NULL AND o.location_id=$1
              AND ($2::uuid IS NULL OR c.position_id=$2)
              AND ($3::integer IS NULL OR o.expected_purchase_revision <= $3)
            ORDER BY o.observed_at DESC,c.id
            """, r => new StockShortageCase(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),"open",r.GetGuid(0),
            r.GetDateTime(3),r.GetInt32(4),r.GetGuid(5),r.GetDateTime(6),r.GetInt32(7)),
            token, locationId, Nullable(NpgsqlDbType.Uuid, positionId), Nullable(NpgsqlDbType.Integer, witnessRevision));

        IReadOnlyList<StockShortageClosureSummary>? closureRows = null;
        if (closures)
            closureRows = observations.Count == 0 ? [] :
                await StockShortageClosures.ReadSummaries(connection, tx, observations, token);
        return new BinCountSnapshot("bin-counts", asOf, true, location, positionId, next, positions, observations, shortages, closureRows);
    }

    internal static StockCountObservation ReadObservation(NpgsqlDataReader r)
    {
        var ledger = r.GetInt32(8);
        var counted = r.GetInt32(10);
        return new StockCountObservation(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),r.GetGuid(4),
            r.GetGuid(5),r.GetString(6),r.GetInt32(7),ledger,r.GetInt32(9),counted,Math.Max(0,ledger-counted),
            Math.Max(0,counted-ledger),r.GetString(11),r.GetDateTime(12),PostedAdjustment: false);
    }
}
