using System.Data;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class Receiving(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(ReceivingCommand request, Guid correlationId, CancellationToken token)
    {
        request.Validate();
        var digest = request.Digest();
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (await CommandJournal.Replay(connection, tx, request.CommandId, digest, token) is { } replay)
            return replay;

        var id = Guid.NewGuid();
        var revision = 1;
        if (request is PurchaseChange change)
        {
            await using var advance = Query(connection, tx, """
                UPDATE brickvault.purchases SET revision=revision+1
                WHERE id=$1 AND revision=$2 RETURNING revision
                """, change.PurchaseId, change.ExpectedRevision);
            revision = (await advance.ExecuteScalarAsync(token)) is int version ? version :
                throw Rejected("revision_conflict", "This purchase has changed. Reload its current revision.");
        }

        try
        {
            if (request is OfferCommand offer)
            {
                await SaveOffer(connection, tx, id, offer, token);
            }
            else
            {
                await using var insert = request switch
                {
                    PurchaseCommand p => Query(connection, tx, """
                        INSERT INTO brickvault.purchases(id,command_id,revision,reference,source,currency,
                            ordered_quantity,pending_charge_minor)
                        VALUES ($1,$2,1,$3,$4,$5,$6,$7)
                        """, id, p.CommandId, p.Reference, p.Source, p.Currency, p.OrderedQuantity,
                        Nullable(NpgsqlDbType.Integer, p.PendingChargeMinor)),
                    DeliveryCommand d => Query(connection, tx, """
                        INSERT INTO brickvault.deliveries(id,command_id,purchase_id,reference,quantity,
                            catalog_id,item_cost_minor)
                        VALUES ($1,$2,$3,$4,$5,$6,$7)
                        """, id, d.CommandId, d.PurchaseId, d.Reference, d.Quantity,
                        Nullable(NpgsqlDbType.Uuid, d.CatalogId), Nullable(NpgsqlDbType.Integer, d.ItemCostMinor)),
                    PutawayCommand p => Query(connection, tx, """
                        INSERT INTO brickvault.placements(id,command_id,purchase_id,delivery_id,catalog_id,
                            location_id,location_code,quantity,confirmed_catalog_revision)
                        VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
                        """, id, p.CommandId, p.PurchaseId, p.DeliveryId, p.CatalogId, p.LocationId,
                        p.ConfirmedLocationCode, p.Quantity, Nullable(NpgsqlDbType.Integer, p.ConfirmedCatalogRevision)),
                    _ => throw new InvalidOperationException()
                };
                await insert.ExecuteNonQueryAsync(token);
            }

            var receipt = new Receipt(request.CommandId, id, revision, request.Kind, correlationId);
            await CommandJournal.Append(connection, tx, receipt, digest, "record-receiving", token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw Rejected("duplicate_receiving_reference", "This purchase or delivery reference already exists.");
        }
        catch (PostgresException ex) when (ex.ConstraintName is
            "receiving_quantity" or "receiving_catalog" or "receiving_catalog_reconfirmation" or
            "receiving_delivery" or "receiving_bin" or "receiving_backing")
        {
            throw ex.ConstraintName switch
            {
                "receiving_quantity" => Rejected("quantity_unavailable", "There are fewer units available for this action."),
                "receiving_catalog" => Rejected("catalog_ineligible", "Confirm a matching, eligible catalog identity and condition."),
                "receiving_catalog_reconfirmation" => Rejected("catalog_reconfirmation_required",
                    "Review the current catalog identity and confirm its revision before putting away unidentified or changed stock."),
                "receiving_delivery" => Rejected("delivery_mismatch", "Select a delivery from this purchase."),
                "receiving_bin" => Rejected("bin_confirmation_mismatch", "The confirmed code must match the selected bin."),
                _ => Rejected("backing_mismatch", "Select matching stock placements that are available to back this offer.")
            };
        }
    }

    private static Fault Rejected(string code, string message) => new(409, code, message);

    private static async Task SaveOffer(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id,
        OfferCommand offer, CancellationToken token)
    {
        await using var create = Query(connection, tx, """
            INSERT INTO brickvault.offers(id,command_id,purchase_id,catalog_id,catalog_revision,unit_price_minor)
            SELECT $1,$2,$3,id,revision,$5 FROM brickvault.catalog WHERE id=$4 AND integration_eligible FOR SHARE
            RETURNING catalog_revision
            """, id, offer.CommandId, offer.PurchaseId, offer.CatalogId, offer.UnitPriceMinor);
        if (await create.ExecuteScalarAsync(token) is not int revision)
            throw Rejected("catalog_ineligible", "Confirm a matching, eligible catalog identity and condition.");
        await using var backing = Query(connection, tx, """
            INSERT INTO brickvault.offer_backing(purchase_id,offer_id,placement_id,catalog_id,catalog_revision,quantity)
            SELECT $1,$2,placement_id,$3,$4,quantity
              FROM unnest($5::uuid[],$6::integer[]) AS placements(placement_id,quantity)
            ORDER BY placement_id
            """, offer.PurchaseId, id, offer.CatalogId, revision,
            offer.Backing.Select(b => b.PlacementId).ToArray(), offer.Backing.Select(b => b.Quantity).ToArray());
        await backing.ExecuteNonQueryAsync(token);
    }

    public async Task<ReceivingWorkspace> Snapshot(CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await Migration.CheckWithin(connection, tx, token);
        await using var clock = Query(connection, tx, "SELECT transaction_timestamp()");
        var asOf = (DateTime)(await clock.ExecuteScalarAsync(token))!;

        var purchases = await Rows(connection, tx, """
            SELECT id,revision,reference,source,currency,ordered_quantity,pending_charge_minor
            FROM brickvault.purchases ORDER BY reference,id
            """, r => (Id: r.GetGuid(0), Revision: r.GetInt32(1), Reference: r.GetString(2),
            Source: r.GetString(3), Currency: r.GetString(4), Ordered: r.GetInt32(5),
            Charge: Optional<int>(r,6)), token);
        var placements = (await Rows(connection, tx, """
            SELECT delivery_id,p.id,p.catalog_id,p.catalog_revision,p.location_id,p.location_code,p.quantity,
                COALESCE((SELECT sum(b.quantity) FROM brickvault.offer_backing b WHERE b.placement_id=p.id),0)::integer,
                c.integration_eligible AND c.revision=p.catalog_revision
            FROM brickvault.placements p JOIN brickvault.catalog c ON c.id=p.catalog_id
            ORDER BY p.location_code,p.id
            """, r =>
        {
            var quantity = r.GetInt32(6);
            var backed = r.GetInt32(7);
            return (DeliveryId: r.GetGuid(0), Value: new PlacementRow(r.GetGuid(1), r.GetGuid(2), r.GetInt32(3),
                r.GetGuid(4), r.GetString(5), quantity, backed, r.GetBoolean(8) ? quantity - backed : 0));
        }, token)).ToLookup(r => r.DeliveryId, r => r.Value);
        var deliveries = (await Rows(connection, tx, """
            SELECT purchase_id,id,reference,quantity,catalog_id,catalog_revision,item_cost_minor
            FROM brickvault.deliveries ORDER BY reference,id
            """, r =>
        {
            var quantity = r.GetInt32(3);
            var positions = placements[r.GetGuid(1)].ToList();
            return (PurchaseId: r.GetGuid(0), Value: new DeliveryRow(r.GetGuid(1), r.GetString(2), quantity,
                Optional<Guid>(r,4), Optional<int>(r,5), Optional<int>(r,6), quantity - positions.Sum(p => p.Quantity), positions));
        }, token)).ToLookup(r => r.PurchaseId, r => r.Value);
        var backing = (await Rows(connection, tx, """
            SELECT offer_id,placement_id,quantity FROM brickvault.offer_backing ORDER BY offer_id,placement_id
            """, r => (OfferId: r.GetGuid(0), Value: new BackingRow(r.GetGuid(1), r.GetInt32(2))),
            token)).ToLookup(r => r.OfferId, r => r.Value);
        var offers = (await Rows(connection, tx, """
            SELECT o.purchase_id,o.id,o.catalog_id,o.catalog_revision,p.currency,o.unit_price_minor,
                c.integration_eligible AND c.revision=o.catalog_revision
            FROM brickvault.offers o JOIN brickvault.purchases p ON p.id=o.purchase_id
            JOIN brickvault.catalog c ON c.id=o.catalog_id ORDER BY o.id
            """, r =>
        {
            var lines = backing[r.GetGuid(1)].ToList();
            return (PurchaseId: r.GetGuid(0), Value: new OfferRow(r.GetGuid(1), r.GetGuid(2), r.GetInt32(3),
                r.GetString(4), r.GetInt32(5), lines, lines.Sum(a => a.Quantity), PublishedQuantity: 0, BackingCurrent: r.GetBoolean(6)));
        }, token)).ToLookup(r => r.PurchaseId, r => r.Value);
        return new ReceivingWorkspace("receiving", asOf, true, purchases.Select(p =>
        {
            var received = deliveries[p.Id].ToList();
            return new PurchaseRow(p.Id, p.Revision, p.Reference, p.Source, p.Currency, p.Ordered, p.Charge,
                received, offers[p.Id].ToList(), Balance(received, p.Charge));
        }).ToList());
    }

    private static StockBalances Balance(IReadOnlyList<DeliveryRow> deliveries, int? pendingChargeMinor)
    {
        var onHand = deliveries.Sum(d => d.Quantity);
        var unavailable = deliveries.Sum(d => d.IntakeQuantity);
        var free = onHand - unavailable;
        var backed = deliveries.SelectMany(d => d.Placements).Sum(p => p.BackedQuantity);
        var documented = deliveries.Where(d => d.ItemCostMinor.HasValue).ToList();
        var knownFree = documented.Sum(d => d.Quantity - d.IntakeQuantity);
        long? known = documented.Count == 0 ? null : documented.Sum(d => (long)d.ItemCostMinor!.Value);
        long? components = known.HasValue || pendingChargeMinor.HasValue
            ? known.GetValueOrDefault() + pendingChargeMinor.GetValueOrDefault() : null;
        return new StockBalances(onHand, unavailable, Reserved: 0, free, backed, free - backed,
            PublishedQuantity: 0, knownFree, free - knownFree, known, components);
    }
}
