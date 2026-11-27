using Npgsql;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class Transfers(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(TransferCommand request, Guid correlationId, CancellationToken token)
    {
        request.Validate();
        var digest = request.Digest();
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (await CommandJournal.Replay(connection, tx, request.CommandId, digest, token) is { } replay) return replay;

        await using var advance = Query(connection, tx, """
            UPDATE brickvault.purchases SET revision=revision+1
            WHERE id=$1 AND revision=$2 RETURNING revision
            """, request.PurchaseId, request.ExpectedRevision);
        if (await advance.ExecuteScalarAsync(token) is not int revision)
            throw Fault.Conflict();

        var id = Guid.NewGuid();
        try
        {
            await using (var movement = Query(connection, tx, """
                INSERT INTO brickvault.placement_transfers(id,command_id,purchase_id,source_position_id,
                    source_location_id,source_location_code,destination_location_id,destination_location_code,quantity,reason)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                """, id, request.CommandId, request.PurchaseId, request.SourcePlacementId,
                request.SourceLocationId, request.ConfirmedSourceLocationCode, request.DestinationLocationId,
                request.ConfirmedDestinationLocationCode, request.Quantity, request.Reason))
                await movement.ExecuteNonQueryAsync(token);

            if (request.MoveAllBacking)
            {
                // The purchase row is still locked and the move has provisionally reduced the source.
                await using var remaining = Query(connection, tx, """
                    SELECT quantity FROM brickvault.current_placement_stock WHERE id=$1
                    """, request.SourcePlacementId);
                if (await remaining.ExecuteScalarAsync(token) is not 0)
                    throw new Fault(409, "transfer_all_backing_requires_full",
                        "Moving all backing requires the full source quantity. Choose offer quantities for a partial transfer.");
                await using var all = Query(connection, tx, """
                    INSERT INTO brickvault.placement_transfer_backing(transfer_id,offer_id,quantity)
                    SELECT $1,offer_id,quantity FROM brickvault.current_offer_backing
                    WHERE placement_id=$2 ORDER BY offer_id
                    """, id, request.SourcePlacementId);
                await all.ExecuteNonQueryAsync(token);
            }
            else if (request.Backing.Length > 0)
            {
                await using var backing = Query(connection, tx, """
                    INSERT INTO brickvault.placement_transfer_backing(transfer_id,offer_id,quantity)
                    SELECT $1,offer_id,quantity FROM unnest($2::uuid[],$3::integer[]) AS lines(offer_id,quantity)
                    ORDER BY offer_id
                    """, id, request.Backing.Select(a => a.OfferId).ToArray(),
                    request.Backing.Select(a => a.Quantity).ToArray());
                await backing.ExecuteNonQueryAsync(token);
            }
            var receipt = new Receipt(request.CommandId, id, revision, "transfer", correlationId);
            await CommandJournal.Append(connection, tx, receipt, digest, "record-receiving", token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException ex) when (ex.ConstraintName is
            "transfer_source" or "transfer_quantity" or "transfer_bin" or "transfer_backing" or "stock_shortage_open" or
            "reserved_stock_unavailable" or "quarantine_capacity_unavailable" or "location_destination_archived")
        {
            throw ex.ConstraintName switch
            {
                "location_destination_archived" => LocationRegistry.ArchivedDestination(),
                "stock_shortage_open" => Fault.StockShortageOpen(),
                "quarantine_capacity_unavailable" => StockQuarantines.ProtectedCapacity(),
                "reserved_stock_unavailable" => new Fault(409, "reserved_stock_unavailable",
                    "Reduce the movement so all remaining backing and build holds stay at the source position."),
                "transfer_source" => new Fault(409, "transfer_source_mismatch", "The selected placement does not belong to this purchase and source bin."),
                "transfer_quantity" => new Fault(409, "transfer_quantity_unavailable", "The selected source placement has fewer units available."),
                "transfer_bin" => new Fault(409, "transfer_bin_confirmation_mismatch", "The confirmed codes must match both registered bins."),
                _ => new Fault(409, "transfer_backing_unavailable", "Choose how many units of each offer move, within the backed and unbacked quantities available.")
            };
        }
    }
}
