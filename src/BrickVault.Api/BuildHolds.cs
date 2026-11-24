using System.Data;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class BuildHolds(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(BuildHoldCommand command, Guid correlationId, CancellationToken token)
    {
        command.Validate();
        var digest = command.Digest();
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (await CommandJournal.Replay(connection, tx, command.CommandId, digest, token) is { } replay) return replay;

        var id = Guid.NewGuid();
        try
        {
            await using var insert = command switch
            {
                StockHoldCommand hold => Query(connection, tx, """
                    INSERT INTO brickvault.stock_build_holds(id,command_id,purchase_id,expected_purchase_revision,
                        quantity,reason,position_id,location_id,location_code,purpose)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                    """, id, hold.CommandId, hold.PurchaseId, hold.ExpectedRevision, hold.Quantity, hold.Reason,
                    hold.PositionId, hold.LocationId, hold.ConfirmedLocationCode, hold.Purpose),
                StockHoldReleaseCommand release => Query(connection, tx, """
                    INSERT INTO brickvault.stock_build_hold_releases(id,command_id,purchase_id,expected_purchase_revision,
                        quantity,reason,hold_id,shortage_id,expected_observation_id)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
                    """, id, release.CommandId, release.PurchaseId, release.ExpectedRevision, release.Quantity, release.Reason,
                    release.HoldId, Nullable(NpgsqlDbType.Uuid, release.ShortageId),
                    Nullable(NpgsqlDbType.Uuid, release.ExpectedObservationId)),
                _ => throw new InvalidOperationException()
            };
            await insert.ExecuteNonQueryAsync(token);
            var receipt = new Receipt(command.CommandId, id, checked(command.ExpectedRevision + 1), command.Kind, correlationId);
            await CommandJournal.Append(connection, tx, receipt, digest, "record-receiving", token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException ex) when (ex.ConstraintName is "hold_revision" or "hold_position" or "hold_bin" or
            "hold_catalog" or "hold_quantity" or "hold_shortage_frontier" or "stock_shortage_open")
        {
            throw ex.ConstraintName switch
            {
                "hold_revision" => StalePurchase(),
                "hold_position" => new Fault(409, "hold_position_mismatch", "Select a hold or identified receipt position from this purchase and bin."),
                "hold_bin" => new Fault(409, "hold_bin_confirmation_mismatch", "Confirm the current code of the selected registered bin."),
                "hold_catalog" => new Fault(409, "hold_catalog_ineligible", "Select a position with current, eligible identification."),
                "hold_quantity" => new Fault(409, "hold_quantity_unavailable", "Choose a quantity within the available units or the named hold's remaining units."),
                "hold_shortage_frontier" => new Fault(409, "hold_shortage_frontier_conflict", "Reload the shortage and confirm its latest observation."),
                _ => Fault.StockShortageOpen()
            };
        }
    }

    private static Fault StalePurchase() =>
        new(409, "revision_conflict", "This purchase has changed. Reload the selected position and confirm the hold action.");

    public async Task<StockHoldPositionSnapshot> Snapshot(Guid locationId, Guid positionId, Guid? after, int limit,
        CancellationToken token, Guid? commandId = null)
    {
        RequirePage(limit, after, commandId);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await Migration.CheckWithin(connection, tx, token);
        var positions = await Rows(connection, tx, """
            SELECT transaction_timestamp(),l.id,l.revision,l.code,l.name,p.purchase_id,buy.revision,p.quantity,
                COALESCE((SELECT sum(b.quantity) FROM brickvault.current_offer_backing b WHERE b.placement_id=p.id),0)::integer,
                COALESCE((SELECT sum(h.remaining_quantity) FROM brickvault.current_build_holds h WHERE h.position_id=p.id),0)::integer,
                c.integration_eligible AND c.revision=p.catalog_revision,s.id,
                (SELECT n.id FROM brickvault.stock_count_observations n WHERE n.position_id=p.id AND s.id IS NOT NULL
                 ORDER BY n.expected_purchase_revision DESC LIMIT 1),
                buy.reference,d.reference,p.catalog_revision,c.id,c.revision,c.item_type,c.item_number,c.variant,c.name,
                c.color_id,c.color_state,c.condition,c.completeness,c.source,c.license,c.mapping,c.integration_eligible
            FROM brickvault.current_placement_stock p
            JOIN brickvault.locations l ON l.id=p.location_id
            JOIN brickvault.purchases buy ON buy.id=p.purchase_id
            JOIN brickvault.deliveries d ON d.id=p.delivery_id
            JOIN brickvault.catalog c ON c.id=p.catalog_id
            LEFT JOIN brickvault.stock_shortage_cases s ON s.position_id=p.id AND s.resolved_by_command_id IS NULL
            WHERE p.id=$1 AND p.location_id=$2
            """, r => (AsOf: r.GetDateTime(0), Location: new LocationRow(r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4)),
                Purchase: r.GetGuid(5), Revision: r.GetInt32(6), Ledger: r.GetInt32(7), Backed: r.GetInt32(8),
                Reserved: r.GetInt32(9), Eligible: r.GetBoolean(10), Shortage: Optional<Guid>(r, 11), Latest: Optional<Guid>(r, 12),
                PurchaseRef: r.GetString(13), DeliveryRef: r.GetString(14), CatalogRevision: r.GetInt32(15), CurrentCatalog: References.ReadCatalog(r, 16)),
            token, positionId, locationId);
        if (positions.Count == 0)
            throw new Fault(404, "unknown_hold_position", "No receipt position with that identifier belongs to this bin.");
        var position = positions.Single();

        var cursor = await Cursor(connection, tx, """
            SELECT expected_purchase_revision FROM brickvault.stock_build_holds WHERE id=$1 AND position_id=$2
            """, after, positionId, token);
        var holds = await Rows(connection, tx, """
            SELECT h.id,h.command_id,c.correlation_id,h.purpose,h.reason,h.quantity,h.released_quantity,h.remaining_quantity,
                a.occurred_at,h.expected_purchase_revision,h.location_code
            FROM brickvault.current_build_holds h JOIN brickvault.commands c ON c.command_id=h.command_id
            JOIN brickvault.audit a ON a.command_id=h.command_id
            WHERE h.position_id=$1 AND ($2::integer IS NULL OR h.expected_purchase_revision<$2)
              AND ($4::uuid IS NULL OR h.command_id=$4)
            ORDER BY h.expected_purchase_revision DESC LIMIT $3
            """, r => new StockHoldRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4),
                r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetDateTime(8), r.GetInt32(9), r.GetString(10)),
            token, positionId, Nullable(NpgsqlDbType.Integer, cursor), limit + 1, Nullable(NpgsqlDbType.Uuid, commandId));
        RequireMatch(holds.Count, commandId);
        var next = Next(holds, limit, h => h.Id);
        var free = position.Ledger - position.Reserved;
        var available = position.Eligible && position.Shortage is null ? free - position.Backed : 0;
        return new StockHoldPositionSnapshot("position-holds", position.AsOf, true, position.Location,
            positionId, position.Purchase, position.Revision, position.PurchaseRef, position.DeliveryRef, position.CatalogRevision,
            position.CurrentCatalog, position.Ledger, position.Backed, position.Reserved,
            free, available, position.Shortage, position.Latest, holds, next);
    }

    public async Task<StockHoldReleasePage> Releases(Guid locationId, Guid positionId, Guid holdId, Guid? after,
        int limit, CancellationToken token, Guid? commandId = null)
    {
        RequirePage(limit, after, commandId);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await Migration.CheckWithin(connection, tx, token);
        await using var hold = Query(connection, tx, """
            SELECT transaction_timestamp() FROM brickvault.stock_build_holds
            WHERE id=$1 AND position_id=$2 AND location_id=$3
            """, holdId, positionId, locationId);
        if (await hold.ExecuteScalarAsync(token) is not DateTime asOf)
            throw new Fault(404, "unknown_hold", "No such hold belongs to the selected receipt position and bin.");
        var cursor = await Cursor(connection, tx, """
            SELECT expected_purchase_revision FROM brickvault.stock_build_hold_releases WHERE id=$1 AND hold_id=$2
            """, after, holdId, token);
        var releases = await Rows(connection, tx, """
            SELECT r.id,r.command_id,c.correlation_id,r.quantity,r.reason,r.shortage_id,r.expected_observation_id,
                a.occurred_at,r.purchase_id,r.expected_purchase_revision
            FROM brickvault.stock_build_hold_releases r JOIN brickvault.commands c ON c.command_id=r.command_id
            JOIN brickvault.audit a ON a.command_id=r.command_id
            WHERE r.hold_id=$1 AND ($2::integer IS NULL OR r.expected_purchase_revision<$2)
              AND ($4::uuid IS NULL OR r.command_id=$4)
            ORDER BY r.expected_purchase_revision DESC LIMIT $3
            """, r => new StockHoldReleaseRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetString(4),
                Optional<Guid>(r, 5), Optional<Guid>(r, 6), r.GetDateTime(7), r.GetGuid(8), r.GetInt32(9)),
            token, holdId, Nullable(NpgsqlDbType.Integer, cursor), limit + 1, Nullable(NpgsqlDbType.Uuid, commandId));
        RequireMatch(releases.Count, commandId);
        return new StockHoldReleasePage("hold-releases", asOf, true, holdId, releases, Next(releases, limit, r => r.Id));
    }

    private static void RequirePage(int limit, Guid? after, Guid? commandId)
    {
        if (limit is < 1 or > 100 || (after.HasValue && commandId.HasValue))
            throw new Fault(400, "invalid_hold_page", "Choose a page size from 1 to 100 or look up one command without a cursor.");
    }

    private static void RequireMatch(int count, Guid? commandId)
    {
        if (commandId.HasValue && count == 0)
            throw new Fault(404, "unknown_hold_command", "No matching hold command exists in the selected scope.");
    }

    private static async Task<int?> Cursor(NpgsqlConnection connection, NpgsqlTransaction tx, string sql,
        Guid? after, Guid scope, CancellationToken token)
    {
        if (after is null) return null;
        await using var query = Query(connection, tx, sql, after.Value, scope);
        return await query.ExecuteScalarAsync(token) as int? ??
            throw new Fault(400, "invalid_hold_cursor", "Use the next cursor from the selected hold or position.");
    }

    private static Guid? Next<T>(List<T> rows, int limit, Func<T, Guid> id)
    {
        if (rows.Count <= limit) return null;
        rows.RemoveAt(limit);
        return id(rows[^1]);
    }
}
