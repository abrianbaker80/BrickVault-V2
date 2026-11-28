using System.Data;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class Receiving(NpgsqlDataSource db)
{
    public Task<Receipt> Save(ReceivingCommand request, Guid correlationId, CancellationToken token) =>
        Save(request, correlationId, token, priceScaleSupported: false);

    public async Task<Receipt> Save(ReceivingCommand request, Guid correlationId, CancellationToken token, bool priceScaleSupported)
    {
        request.Validate();
        var digest = request.Digest();
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (await CommandJournal.Replay(connection, tx, request.CommandId, digest, token) is { } replay)
            return replay;

        if (request is OfferCommand pricing)
        {
            var currency = pricing.Currency;
            if (currency is null)
            {
                // Existing purchase currency is immutable; no write has occurred on a rejected offer.
                await using var basis = Query(connection, tx, "SELECT currency FROM brickvault.purchases WHERE id=$1", pricing.PurchaseId);
                currency = await basis.ExecuteScalarAsync(token) as string ??
                    throw Rejected("revision_conflict", "This purchase has changed. Reload its current revision.");
            }
            OfferPriceScale.CheckNewOffer(currency, pricing.Currency is not null, priceScaleSupported);
        }

        var id = Guid.NewGuid();
        var revision = 1;
        if (request is PooledOfferCommand pool)
        {
            revision = await PooledOffers.AdvanceSources(connection, tx, pool, token);
        }
        else if (request is PurchaseChange change)
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
                            catalog_id,item_cost_minor,set_contents)
                        VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
                        """, id, d.CommandId, d.PurchaseId, d.Reference, d.Quantity,
                        Nullable(NpgsqlDbType.Uuid, d.CatalogId), Nullable(NpgsqlDbType.Integer, d.ItemCostMinor), SetContents.Parameter(d.SetContents)),
                    PutawayCommand p => Query(connection, tx, """
                        INSERT INTO brickvault.placements(id,command_id,purchase_id,delivery_id,catalog_id,
                            location_id,location_code,quantity,confirmed_catalog_revision,set_contents)
                        VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                        """, id, p.CommandId, p.PurchaseId, p.DeliveryId, p.CatalogId, p.LocationId,
                        p.ConfirmedLocationCode, p.Quantity, Nullable(NpgsqlDbType.Integer, p.ConfirmedCatalogRevision), SetContents.Parameter(p.SetContents)),
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
            "receiving_delivery" or "receiving_bin" or "receiving_backing" or "stock_shortage_open" or "reserved_stock_unavailable" or
            "quarantine_capacity_unavailable" or "location_destination_archived" or "location_capacity_exceeded" or
            "set_contents_unverified" or "set_contents_mismatch" or "set_contents_not_set")
        {
            throw ex.ConstraintName switch
            {
                "location_destination_archived" => LocationRegistry.ArchivedDestination(),
                "location_capacity_exceeded" => LocationCapacities.Rejection(ex),
                "stock_shortage_open" => Fault.StockShortageOpen(),
                "set_contents_unverified" or "set_contents_mismatch" or "set_contents_not_set" => SetContents.Conflict(ex.ConstraintName),
                "quarantine_capacity_unavailable" => StockQuarantines.ProtectedCapacity(),
                "reserved_stock_unavailable" => Rejected("reserved_stock_unavailable", "Choose units that are free of build holds and other backing."),
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
            INSERT INTO brickvault.offers(id,command_id,purchase_id,catalog_id,catalog_revision,unit_price_minor,currency,price_scale_verified,set_contents)
            SELECT $1,$2,p.id,c.id,c.revision,$5,COALESCE($6,p.currency),true,$8
            FROM brickvault.catalog c JOIN brickvault.purchases p ON p.id=$3
            WHERE c.id=$4 AND c.integration_eligible AND ($7::integer IS NULL OR c.revision=$7) FOR SHARE OF c
            RETURNING catalog_revision
            """, id, offer.CommandId, offer.PurchaseId, offer.CatalogId, offer.UnitPriceMinor, Nullable(NpgsqlDbType.Varchar, offer.Currency),
            Nullable(NpgsqlDbType.Integer, (offer as PooledOfferCommand)?.ExpectedCatalogRevision), SetContents.Parameter(offer.SetContents));
        if (await create.ExecuteScalarAsync(token) is not int revision)
            throw Rejected("catalog_ineligible", "Confirm a matching, eligible catalog identity and condition.");
        IEnumerable<OfferSourceCommand> sources = offer is PooledOfferCommand pooled ? pooled.Sources :
            [new OfferSourceCommand(offer.PurchaseId, offer.ExpectedRevision, offer.Backing)];
        var allocations = sources.SelectMany(source => source.Backing.Select(line =>
            (source.PurchaseId, line.PlacementId, line.Quantity))).ToArray();
        await using var backing = Query(connection, tx, """
            INSERT INTO brickvault.offer_backing(purchase_id,offer_id,placement_id,catalog_id,catalog_revision,quantity)
            SELECT purchase_id,$1,placement_id,$2,$3,quantity
              FROM unnest($4::uuid[],$5::uuid[],$6::integer[]) AS placements(purchase_id,placement_id,quantity)
            ORDER BY purchase_id,placement_id
            """, id, offer.CatalogId, revision, allocations.Select(b => b.PurchaseId).ToArray(),
            allocations.Select(b => b.PlacementId).ToArray(), allocations.Select(b => b.Quantity).ToArray());
        await backing.ExecuteNonQueryAsync(token);
    }

    public async Task<ReceivingWorkspace> LegacySnapshot(CancellationToken token)
    {
        var snapshot = await CurrentSnapshot(acceptHolds: false, token);
        if (snapshot.Purchases.SelectMany(p => p.Offers).Any(offer => offer.Backing.Count > 64))
            throw new Fault(426, "receiving_client_upgrade_required",
                "This offer spans more stock positions than this client supports. Update Workbench to read the full receiving snapshot.");
        return snapshot;
    }

    public async Task<ReceivingWorkspace> CurrentSnapshot(bool acceptHolds, CancellationToken token)
    {
        var snapshot = await LossSnapshot(token, recoveries: true);
        if (!acceptHolds && snapshot.Purchases.Any(p => p.Balances.Reserved > 0))
            throw new Fault(426, "receiving_client_upgrade_required",
                "Active build holds require a newer Workbench. Update Workbench to read the reserved stock and receiving balances.");
        if (snapshot.Purchases.SelectMany(p => p.Deliveries).Any(d => d.LostQuantity > 0))
            throw new Fault(426, "receiving_client_upgrade_required",
                "Posted stock losses require a newer Workbench. Update Workbench to distinguish original receipt quantities, posted losses and current stock.");
        if (snapshot.Purchases.SelectMany(p => p.Offers).Any(o => o.Backing.Count == 0))
            throw new Fault(426, "receiving_client_upgrade_required",
                "An offer has no remaining backing. Update Workbench to see the offer and its current allocation.");
        if (snapshot.Purchases.SelectMany(p => p.Deliveries).Any(d => d.RecoveryTotals?.RecoveredQuantity > 0))
            throw StockLossRecoveries.Upgrade();
        return BeforeLossContract(snapshot);
    }

    public Task<ReceivingWorkspace> Snapshot(CancellationToken token) => CurrentSnapshot(true, token);

    private static ReceivingWorkspace BeforeLossContract(LossReceivingWorkspace snapshot) =>
        new(snapshot.Scope, snapshot.AsOf, snapshot.Complete, snapshot.Purchases.Select(p =>
            new PurchaseRow(p.Id, p.Revision, p.Reference, p.Source, p.Currency, p.OrderedQuantity, p.PendingChargeMinor,
                p.Deliveries.Select(d => new DeliveryRow(d.Id, d.Reference, d.Quantity, d.CatalogId, d.CatalogRevision,
                    d.ItemCostMinor, d.IntakeQuantity, d.Placements,d.SetContents)).ToList(), p.Offers, p.Balances)).ToList());

    public async Task<LossReceivingWorkspace> LossSnapshot(CancellationToken token, bool quarantine = false, bool recoveries = false, bool partOuts = false, bool pooledOffers = false)
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
                COALESCE((SELECT sum(b.quantity) FROM brickvault.current_offer_backing b WHERE b.placement_id=p.id),0)::integer,
                c.integration_eligible AND c.revision=p.catalog_revision AND (c.item_type<>'set' OR contents.set_contents IS NOT NULL)
                    AND NOT EXISTS (SELECT 1 FROM brickvault.stock_shortage_cases sc WHERE sc.position_id=p.id AND sc.resolved_by_command_id IS NULL),
                COALESCE((SELECT sum(h.remaining_quantity) FROM brickvault.current_build_holds h WHERE h.position_id=p.id),0)::integer,
                COALESCE((SELECT q.quantity FROM brickvault.current_position_quarantine q WHERE q.position_id=p.id),0),contents.set_contents
            FROM brickvault.current_placement_stock p JOIN brickvault.catalog c ON c.id=p.catalog_id
            LEFT JOIN brickvault.position_set_contents contents ON contents.position_id=p.id
            WHERE p.quantity > 0
            ORDER BY p.location_code,p.id
            """, r =>
        {
            var quantity = r.GetInt32(6);
            var backed = r.GetInt32(7);
            var reserved = r.GetInt32(9);
            var unavailable = r.GetInt32(10);
            return (DeliveryId: r.GetGuid(0), Reserved: reserved, Quarantined: unavailable, Value: new PlacementRow(r.GetGuid(1),
                r.GetGuid(2), r.GetInt32(3), r.GetGuid(4), r.GetString(5), quantity, backed,
                r.GetBoolean(8) ? quantity - backed - reserved - unavailable : 0, SetContents.Read(r,11)));
        }, token)).ToLookup(r => r.DeliveryId);
        var deliveries = (await Rows(connection, tx, """
            SELECT d.purchase_id,d.id,d.reference,d.quantity,d.catalog_id,d.catalog_revision,d.item_cost_minor,
                losses.active_lost_quantity::integer,losses.active_allocated_item_cost_minor::integer,
                d.item_cost_minor-losses.active_allocated_item_cost_minor::integer-po.transformed_out_item_cost_minor,
                losses.gross_lost_quantity::bigint,losses.recovered_quantity::bigint,
                losses.gross_allocated_item_cost_minor::text,losses.restored_item_cost_minor::text,
                po.parent_transformation_id,po.source_delivery_id,po.source_position_id,
                po.transformed_out_quantity,po.transformed_out_item_cost_minor,d.set_contents
            FROM brickvault.deliveries d JOIN brickvault.delivery_loss_balances losses ON losses.delivery_id=d.id
            JOIN brickvault.delivery_part_out_balances po ON po.delivery_id=d.id
            ORDER BY d.reference,d.id
            """, r =>
        {
            var quantity = r.GetInt32(3);
            var positions = placements[r.GetGuid(1)];
            var values = positions.Select(p => p.Value).ToList();
            var lost = r.GetInt32(7);
            var transformed = LayerPartOut.Read(r, 14, partOuts);
            return (PurchaseId: r.GetGuid(0), Reserved: positions.Sum(p => p.Reserved), Quarantined: positions.Sum(p => p.Quarantined),
                Value: new LossDeliveryRow(r.GetGuid(1), r.GetString(2), quantity, Optional<Guid>(r,4), Optional<int>(r,5),
                    Optional<int>(r,6), quantity - lost - (transformed?.ConsumedQuantity ?? 0) - values.Sum(p => p.Quantity), values, lost,
                    Optional<int>(r,8), Optional<int>(r,9), RecoveryTotals: DeliveryRecoveryTotals.Read(r, 10, recoveries), PartOut: transformed, SetContents: SetContents.Read(r,19)));
        }, token)).ToLookup(r => r.PurchaseId);
        var offers = await ReadOffers(connection, tx, pooledOffers, token);
        return new LossReceivingWorkspace("receiving", asOf, true, purchases.Select(p =>
        {
            var layers = deliveries[p.Id];
            var all = layers.Select(d => quarantine ? d.Value with { QuarantinedQuantity = d.Quarantined } : d.Value).ToList();
            var supplier = all.Where(d => d.PartOut?.ParentTransformationId is null).ToList();
            var derived = partOuts ? all.Where(d => d.PartOut?.ParentTransformationId is not null).ToList() : null;
            return new LossPurchaseRow(p.Id, p.Revision, p.Reference, p.Source, p.Currency, p.Ordered, p.Charge,
                supplier, offers[p.Id].ToList(), Balance(all, supplier, layers.Sum(d => (long)d.Reserved),
                    layers.Sum(d => (long)d.Quarantined),
                    layers.Where(d => d.Value.ItemCostMinor.HasValue).Sum(d => (long)d.Reserved + d.Quarantined), p.Charge), derived);
        }).ToList());
    }

    private static async Task<ILookup<Guid, OfferRow>> ReadOffers(NpgsqlConnection connection, NpgsqlTransaction tx,
        bool pooledOffers, CancellationToken token)
    {
        // Original allocations retain source membership after transfer, loss or withdrawal to zero.
        var sources = (await Rows(connection, tx, """
            SELECT DISTINCT offer_id,purchase_id FROM brickvault.offer_backing ORDER BY offer_id,purchase_id
            """, r => (Offer: r.GetGuid(0), Purchase: r.GetGuid(1)), token)).ToLookup(r => r.Offer, r => r.Purchase);
        var backing = (await Rows(connection, tx, """
            SELECT b.offer_id,p.purchase_id,b.placement_id,b.quantity FROM brickvault.current_offer_backing b
            JOIN brickvault.placement_positions p ON p.id=b.placement_id ORDER BY b.offer_id,p.purchase_id,b.placement_id
            """, r => (Offer: r.GetGuid(0), Purchase: r.GetGuid(1), Line: new BackingRow(r.GetGuid(2),r.GetInt32(3))),
            token)).ToLookup(r => (r.Offer,r.Purchase), r => r.Line);
        var offers = await Rows(connection, tx, """
            SELECT o.purchase_id,o.id,o.catalog_id,o.catalog_revision,o.currency,o.unit_price_minor,
                c.integration_eligible AND c.revision=o.catalog_revision,o.price_scale_verified,o.set_contents
            FROM brickvault.offers o JOIN brickvault.catalog c ON c.id=o.catalog_id ORDER BY o.id
            """, r =>
        {
            var id = r.GetGuid(1);
            var contributions = sources[id].Select(purchase =>
            {
                var lines = backing[(id,purchase)].ToList();
                return new OfferSource(purchase, lines, lines.Sum(a => a.Quantity));
            }).ToList();
            OfferPool? pool = null;
            if (contributions.Count > 1)
            {
                if (!pooledOffers) throw PooledOffers.Upgrade();
                pool = new OfferPool(r.GetGuid(0), contributions.Sum(source => source.BackedQuantity), contributions);
            }
            return contributions.Select(source => (source.PurchaseId, Value: new OfferRow(id,r.GetGuid(2),
                r.GetInt32(3),r.GetString(4),r.GetInt32(5),source.Backing,source.BackedQuantity,PublishedQuantity: 0,
                BackingCurrent: (pool?.TotalBackedQuantity ?? source.BackedQuantity) > 0 && r.GetBoolean(6),
                PriceScaleVerified: OfferPriceScale.Verified(r.GetString(4), r.GetBoolean(7)), Pool: pool, SetContents: SetContents.Read(r,8)))).ToList();
        }, token);
        return offers.SelectMany(rows => rows).ToLookup(r => r.PurchaseId, r => r.Value);
    }

    private static StockBalances Balance(IReadOnlyList<LossDeliveryRow> deliveries, IReadOnlyList<LossDeliveryRow> supplier,
        long reserved, long quarantined, long knownBlocked, int? pendingChargeMinor)
    {
        var onHand = deliveries.Sum(d => (long)d.Quantity - d.LostQuantity - (d.PartOut?.ConsumedQuantity ?? 0));
        var unavailable = deliveries.Sum(d => (long)d.IntakeQuantity) + quarantined;
        var free = onHand - unavailable - reserved;
        var backed = deliveries.SelectMany(d => d.Placements).Sum(p => (long)p.BackedQuantity);
        var knownFree = deliveries.Where(d => d.ItemCostMinor.HasValue)
            .Sum(d => (long)d.Quantity - d.LostQuantity - (d.PartOut?.ConsumedQuantity ?? 0) - d.IntakeQuantity) - knownBlocked;
        var documentedPurchaseCosts = supplier.Where(d => d.ItemCostMinor.HasValue).ToList();
        long? known = documentedPurchaseCosts.Count == 0 ? null : documentedPurchaseCosts.Sum(d => (long)d.ItemCostMinor!.Value);
        long? components = known.HasValue || pendingChargeMinor.HasValue
            ? known.GetValueOrDefault() + pendingChargeMinor.GetValueOrDefault() : null;
        return new StockBalances(onHand, unavailable, reserved, free, backed, free - backed,
            PublishedQuantity: 0, knownFree, free - knownFree, known, components);
    }
}
