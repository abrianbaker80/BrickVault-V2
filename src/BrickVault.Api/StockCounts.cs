using System.Data;
using Npgsql;
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

        await using var advance = Query(connection, tx, """
            UPDATE brickvault.purchases SET revision=revision+1
            WHERE id=$1 AND revision=$2 RETURNING revision
            """, command.PurchaseId, command.ExpectedRevision);
        if (await advance.ExecuteScalarAsync(token) is not int revision)
            throw StalePurchase();

        try
        {
            var id = Guid.NewGuid();
            await using (var row = Query(connection, tx, """
                INSERT INTO brickvault.stock_count_observations(id,command_id,purchase_id,position_id,location_id,
                    location_code,expected_purchase_revision,counted_quantity,reason)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
                """, id, command.CommandId, command.PurchaseId, command.PositionId, command.LocationId,
                command.ConfirmedLocationCode, command.ExpectedRevision, command.CountedQuantity, command.Reason))
                await row.ExecuteNonQueryAsync(token);
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

    public async Task<BinCountSnapshot> Snapshot(Guid locationId, CancellationToken token)
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

        var positions = await Rows(connection, tx, """
            SELECT p.id,p.purchase_id,buy.reference,d.id,d.reference,buy.revision,p.catalog_id,p.catalog_revision,
                p.quantity,COALESCE((SELECT sum(b.quantity) FROM brickvault.current_offer_backing b
                 WHERE b.placement_id=p.id),0)::integer,
                EXISTS(SELECT 1 FROM brickvault.stock_shortage_cases c WHERE c.position_id=p.id)
            FROM brickvault.current_placement_stock p JOIN brickvault.purchases buy ON buy.id=p.purchase_id
            JOIN brickvault.deliveries d ON d.id=p.delivery_id
            WHERE p.location_id=$1 AND p.quantity>0
            ORDER BY buy.reference,d.reference,p.id
            """, r => new CountPosition(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3),
            r.GetString(4),r.GetInt32(5),r.GetGuid(6),r.GetInt32(7),r.GetInt32(8),r.GetInt32(9),r.GetBoolean(10)),
            token, locationId);

        var observations = await Rows(connection, tx, """
            SELECT n.id,n.command_id,c.correlation_id,n.purchase_id,n.position_id,n.location_id,n.location_code,
                n.expected_purchase_revision,n.ledger_quantity,n.offer_backed_quantity,n.counted_quantity,n.reason,a.occurred_at
            FROM brickvault.stock_count_observations n JOIN brickvault.commands c ON c.command_id=n.command_id
            JOIN brickvault.audit a ON a.command_id=n.command_id
            WHERE n.location_id=$1 ORDER BY a.occurred_at DESC,n.id
            """, r =>
        {
            var ledger = r.GetInt32(8);
            var counted = r.GetInt32(10);
            return new StockCountObservation(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),r.GetGuid(4),
                r.GetGuid(5),r.GetString(6),r.GetInt32(7),ledger,r.GetInt32(9),counted,Math.Max(0,ledger-counted),
                Math.Max(0,counted-ledger),r.GetString(11),r.GetDateTime(12),PostedAdjustment: false);
        }, token, locationId);

        var byId = observations.ToDictionary(x => x.Id);
        var latestByPosition = observations.GroupBy(x => x.PositionId)
            .ToDictionary(x => x.Key,x => x.MaxBy(o => o.ExpectedPurchaseRevision)!);
        var shortages = await Rows(connection, tx, """
            SELECT c.id,c.position_id
            FROM brickvault.stock_shortage_cases c JOIN brickvault.stock_count_observations o ON o.id=c.id
            JOIN brickvault.audit a ON a.command_id=o.command_id
            WHERE o.location_id=$1 ORDER BY a.occurred_at DESC,c.id
            """, r =>
        {
            var opened = byId[r.GetGuid(0)];
            var latest = latestByPosition[r.GetGuid(1)];
            return new StockShortageCase(opened.Id,opened.PositionId,opened.PurchaseId,"open",opened.Id,opened.ObservedAt,
                opened.MissingQuantity,latest.Id,latest.ObservedAt,latest.MissingQuantity);
        }, token, locationId);
        return new BinCountSnapshot("bin-counts", asOf, true, location, positions, observations, shortages);
    }
}
