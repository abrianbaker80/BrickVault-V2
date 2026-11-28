using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

[Collection("Build holds")]
public sealed class DraftBackingWithdrawalTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task WithdrawalReleasesOnlyNamedBackingAndPreservesHistoricalLossAmounts()
    {
        var connection = Environment.GetEnvironmentVariable("BV_TEST_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(connection), "BV_TEST_DB with a dedicated bv_m2_test_ database is required.");
        var settings = new NpgsqlConnectionStringBuilder(connection);
        Assert.True(settings.Host is "127.0.0.1" or "::1" && settings.Username == "bv_api" &&
            settings.Database?.StartsWith("bv_m2_test_", StringComparison.Ordinal) == true);
        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(connection!);
        var references = new References(db);
        var receiving = new Receiving(db);
        var drafts = new DraftBackingWithdrawals(db);
        var transfers = new Transfers(db);
        var losses = new StockLosses(db);
        var timelines = new StockPositionTimelines(db);
        var lots = new StockReceiptLots(db);
        var tag = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var item = await references.Save(new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = tag, Variant = null,
            Name = "Fictional draft backing fixture", ColorId = 0, ColorState = "known", Condition = "new",
            Completeness = "complete", Source = "Fictional reference", License = "operator-owned", Mapping = "confirmed"
        }, Guid.NewGuid(), token);
        async Task<(Guid Id, string Code)> Bin(string suffix)
        {
            var code = $"DRAFT-{tag}-{suffix}";
            var registration = await references.Save(new LocationCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code, Name = "Fictional bin"
            }, Guid.NewGuid(), token);
            return (registration.EntityId, code);
        }
        var a = await Bin("A");
        var b = await Bin("B");
        var c = await Bin("C");
        var d = await Bin("D");
        var bulk = await Bin("BULK");
        var purchase = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "DRAFT-" + tag,
            Source = "Fictional supplier", Currency = "USD", OrderedQuantity = 113, PendingChargeMinor = 66
        }, Guid.NewGuid(), token);
        Task<Receipt> Deliver(int revision, string label, int quantity, int? cost) => receiving.Save(new DeliveryCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            Reference = label, Quantity = quantity, CatalogId = item.EntityId, ItemCostMinor = cost
        }, Guid.NewGuid(), token);
        Task<Receipt> Place(int revision, Receipt delivery, (Guid Id, string Code) bin, int quantity) =>
            receiving.Save(new PutawayCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
                DeliveryId = delivery.EntityId, CatalogId = item.EntityId, LocationId = bin.Id,
                ConfirmedLocationCode = bin.Code, Quantity = quantity
            }, Guid.NewGuid(), token);
        OfferCommand Offer(int revision, int price, params BackingRow[] backing) => new()
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            CatalogId = item.EntityId, UnitPriceMinor = price, Backing = backing
        };
        Task<Receipt> Price(OfferCommand request) => receiving.Save(request, Guid.NewGuid(), token);
        async Task<string> Reject(Func<Task> action) => (await Assert.ThrowsAsync<Fault>(action)).Code;

        var delivery = await Deliver(purchase.Revision, "Documented zero", 12, 0);
        var origin = await Place(delivery.Revision, delivery, a, 8);
        var sibling = await Place(origin.Revision, delivery, b, 4);
        var firstPrice = Offer(sibling.Revision, 249, new BackingRow(origin.EntityId, 3), new(sibling.EntityId, 2)) with { Currency = "KWD" };
        var firstOffer = await Price(firstPrice);
        Assert.Equal(firstOffer, await Price(firstPrice));
        Assert.Equal("command_mismatch", await Reject(() => Price(firstPrice with { Currency = "JPY" })));
        var otherOffer = await Price(Offer(firstOffer.Revision, 300, new BackingRow(sibling.EntityId, 1)));
        var hold = await new BuildHolds(db).Save(new StockHoldCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = otherOffer.Revision, PurchaseId = purchase.EntityId,
            PositionId = origin.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code,
            Quantity = 2, Purpose = "Fictional build", Reason = "Keep build stock"
        }, Guid.NewGuid(), token);
        DraftBackingWithdrawalCommand Prepare(int revision, Guid positionId, Guid offerId, (Guid Id, string Code) bin) => new()
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            PositionId = positionId, OfferId = offerId, LocationId = bin.Id, ConfirmedLocationCode = bin.Code,
            Quantity = 1, Reason = "Withdraw one reviewed draft unit"
        };
        Task<Receipt> Save(DraftBackingWithdrawalCommand command) => drafts.Save(command, Guid.NewGuid(), token);
        var command = Prepare(hold.Revision, origin.EntityId, firstOffer.EntityId, a);
        Assert.Equal("draft_backing_bin_conflict", await Reject(() => Save(command with { ConfirmedLocationCode = b.Code })));
        Assert.Equal("draft_backing_position_mismatch", await Reject(() => Save(command with { LocationId = b.Id })));
        Assert.Equal("draft_backing_offer_mismatch", await Reject(() => Save(command with { OfferId = otherOffer.EntityId })));
        Assert.Equal("draft_backing_quantity_unavailable", await Reject(() => Save(command with { Quantity = 4 })));
        Assert.Equal("revision_conflict", await Reject(() => Save(command with { ExpectedRevision = firstOffer.Revision })));
        Assert.Equal("invalid_draft_backing_withdrawal", await Reject(() => Save(command with { Reason = " " })));
        var replay = await Task.WhenAll(Save(command), Save(command));
        var withdrawal = replay[0];
        Assert.Equal(withdrawal, replay[1]);
        Assert.Equal("command_mismatch", await Reject(() => Save(command with { Reason = "Different decision" })));
        var snapshot = await drafts.Snapshot(origin.EntityId, null, 25, token);
        Assert.Equal((8, 2, 6, 2, 4, 4), (snapshot.PhysicalOnHand, snapshot.HeldQuantity, snapshot.SellableQuantity,
            snapshot.OfferBackedQuantity, snapshot.WithheldQuantity, snapshot.FreeToBackQuantity));
        Assert.Equal(0, snapshot.Position.Layer.ReceiptItemCostMinor);
        Assert.Equal(new DraftBackingOfferRow(firstOffer.EntityId, 249, "KWD", 2, 4, "internal-unpublished", true),
            Assert.Single(snapshot.Offers));
        var evidence = await drafts.Evidence(origin.EntityId, withdrawal.EntityId, null, token);
        Assert.Equal((withdrawal.CorrelationId, withdrawal.Revision, 3, 2, 5, 4, 249, "KWD"),
            (evidence.CorrelationId, evidence.Revision, evidence.PriorBackedQuantity, evidence.RemainingBackedQuantity,
             evidence.PriorTotalOfferBackedQuantity, evidence.RemainingTotalOfferBackedQuantity, evidence.UnitPriceMinor, evidence.Currency));
        Assert.Equal("unknown_draft_backing_withdrawal",
            await Reject(() => drafts.Evidence(sibling.EntityId, null, withdrawal.CommandId, token)));
        var legacy = Assert.Single((await receiving.LossSnapshot(token, recoveries: true)).Purchases, p => p.Id == purchase.EntityId);
        Assert.Equal((12, 2, 10, 5, 5), (legacy.Balances.PhysicalOnHand, legacy.Balances.Reserved,
            legacy.Balances.Free, legacy.Balances.BackedQuantity, legacy.Balances.WithheldQuantity));
        Assert.Equal("draft_backing_client_upgrade_required", await Reject(() => timelines.Read(origin.EntityId, null, 25, token)));
        Assert.Equal("draft_backing_client_upgrade_required", await Reject(() => lots.Read(delivery.EntityId, null, null, 25, 25, token)));
        Assert.DoesNotContain((await timelines.Read(sibling.EntityId, null, 25, token)).Events, x => x.Kind == "offer-withdrawal");

        Task<Receipt> Move(int revision, Guid position, (Guid Id, string Code) from, (Guid Id, string Code) to,
            params TransferBacking[] backing) => transfers.Save(new TransferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            SourcePlacementId = position, SourceLocationId = from.Id, ConfirmedSourceLocationCode = from.Code,
            DestinationLocationId = to.Id, ConfirmedDestinationLocationCode = to.Code, Quantity = 1,
            Reason = "Fictional internal move", Backing = backing
        }, Guid.NewGuid(), token);
        var moved = await Move(withdrawal.Revision, sibling.EntityId, b, c, new TransferBacking(firstOffer.EntityId, 1));
        var bin = await new BinStock(db).Movements(c.Id, token);
        Assert.Equal("USD", Assert.Single(bin.Positions).Layer.Currency);
        Assert.Equal("KWD", Assert.Single(bin.Positions[0].Backing).Currency);
        Assert.Equal("KWD", Assert.Single(Assert.Single(bin.Movements).Backing).Currency);
        var withdrawnFromChild = await Save(Prepare(moved.Revision, moved.EntityId, firstOffer.EntityId, c));
        var onward = await Move(withdrawnFromChild.Revision, moved.EntityId, c, d);
        var child = await drafts.History(moved.EntityId, firstOffer.EntityId, null, 1, token);
        Assert.Equal((0, 0, false), (child.PhysicalOnHand, child.Offer.BackedQuantity, child.Offer.CanWithdraw));
        Assert.Equal("KWD", child.Offer.Currency);
        Assert.Equal("offer-withdrawal", Assert.Single(child.Contributions).Kind);
        Assert.NotNull(child.NextAfter);
        var transferHistory = await drafts.History(moved.EntityId, firstOffer.EntityId, child.NextAfter, 1, token);
        Assert.Equal(("transfer-in", 1, sibling.EntityId), (Assert.Single(transferHistory.Contributions).Kind,
            transferHistory.Contributions[0].BackingDelta, transferHistory.Contributions[0].RelatedPositionId));
        Assert.Equal("invalid_draft_backing_cursor", await Reject(() =>
            drafts.History(sibling.EntityId, firstOffer.EntityId, child.NextAfter, 1, token)));

        var count = await new StockCounts(db).Save(new StockCountCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = onward.Revision, PurchaseId = purchase.EntityId,
            PositionId = origin.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code,
            CountedQuantity = 3, Reason = "Witnessed fictional shortage"
        }, Guid.NewGuid(), token);
        Assert.Equal("stock_shortage_open", await Reject(() => Save(Prepare(count.Revision, origin.EntityId, firstOffer.EntityId, a))));
        Assert.False(Assert.Single((await drafts.Snapshot(origin.EntityId, null, 25, token)).Offers).CanWithdraw);
        var adjustment = await new StockAdjustmentReviews(db).Read(a.Id, origin.EntityId, count.EntityId, null, 25, token);
        Assert.Equal("USD", adjustment.Receipt.Currency);
        Assert.Equal("KWD", Assert.Single(adjustment.Offers).Currency);
        var posting = await losses.Save(new StockLossCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = count.Revision, PurchaseId = purchase.EntityId,
            PositionId = origin.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code, CaseId = count.EntityId,
            ExpectedObservationId = count.EntityId, Reason = "Posted loss preserves build hold and one backed unit",
            OfferReductions = [new(firstOffer.EntityId, 1)]
        }, Guid.NewGuid(), token);
        Task<StockLossSnapshot> ReadLoss() => losses.Read(a.Id, origin.EntityId, null, null, posting.EntityId, null, null, 25, token);
        var originalLoss = await ReadLoss();
        Assert.Equal("USD", originalLoss.Receipt.Currency);
        Assert.Equal((2, 1, 0, 0), (originalLoss.OfferBackedQuantityBefore, originalLoss.OfferBackedQuantityAfter,
            originalLoss.AllocatedItemCostMinor, originalLoss.RemainingDeliveryItemCostMinor));
        Assert.Equal(new StockLossReduction(firstOffer.EntityId, 1, 2, 1, 249, "KWD"), Assert.Single(originalLoss.Reductions));

        async Task<(Receipt? Receipt, string? Error)> Rival()
        {
            try { return (await Save(Prepare(posting.Revision, origin.EntityId, firstOffer.EntityId, a)), null); }
            catch (Fault error) { return (null, error.Code); }
        }
        var rivals = await Task.WhenAll(Rival(), Rival());
        var lastAtOrigin = Assert.Single(rivals, r => r.Receipt is not null).Receipt!;
        Assert.Equal("revision_conflict", Assert.Single(rivals, r => r.Receipt is null).Error);
        Assert.Equal("draft_backing_quantity_unavailable",
            await Reject(() => Save(Prepare(lastAtOrigin.Revision, origin.EntityId, firstOffer.EntityId, a))));
        Assert.Equal("stale_draft_backing_page",
            await Reject(() => drafts.History(moved.EntityId, firstOffer.EntityId, child.NextAfter, 1, token)));
        var reread = await ReadLoss();
        Assert.Equal(originalLoss with { AsOf = reread.AsOf, Reductions = reread.Reductions }, reread);
        Assert.Equal(originalLoss.Reductions, reread.Reductions);
        var allAtOrigin = await drafts.History(origin.EntityId, firstOffer.EntityId, null, 25, token);
        Assert.Equal(new[] { -1, -1, -1, 3 }, allAtOrigin.Contributions.Select(x => x.BackingDelta));
        Assert.Equal(new[] { "offer-withdrawal", "stock-loss", "offer-withdrawal", "offer-backing" },
            allAtOrigin.Contributions.Select(x => x.Kind));
        var lot = await lots.Read(delivery.EntityId, null, null, 25, 25, token, true);
        Assert.Equal((7, 5, 2, 2, 0), (lot.CurrentOnHandQuantity, lot.LostQuantity, lot.OfferBackedQuantity,
            lot.HeldQuantity, lot.ItemCostMinor));
        var timeline = await timelines.Read(origin.EntityId, null, 1, token, true);
        var evt = Assert.Single(timeline.Events);
        Assert.Equal((lastAtOrigin.EntityId, firstOffer.EntityId, "offer-withdrawal", 0, 0, -1),
            (evt.Id, evt.OfferId, evt.Kind, evt.PhysicalDelta, evt.ReservedDelta, evt.OfferBackedDelta));
        Assert.Equal("stock-loss", Assert.Single((await timelines.Read(origin.EntityId, timeline.NextAfter, 1, token, true)).Events).Kind);
        var empty = await Save(Prepare(lastAtOrigin.Revision, sibling.EntityId, firstOffer.EntityId, b));
        var fullyWithdrawn = Assert.Single((await receiving.LossSnapshot(token, recoveries: true)).Purchases, p => p.Id == purchase.EntityId);
        var retained = Assert.Single(fullyWithdrawn.Offers, x => x.Id == firstOffer.EntityId);
        Assert.Equal((0, 249, "KWD", 0), (retained.BackedQuantity, retained.UnitPriceMinor, retained.Currency, retained.PublishedQuantity));
        Assert.Empty(retained.Backing);
        Assert.Equal("USD", fullyWithdrawn.Currency);
        Assert.Equal("USD", Assert.Single(fullyWithdrawn.Offers, x => x.Id == otherOffer.EntityId).Currency);
        Assert.Equal(1, Assert.Single(fullyWithdrawn.Offers, x => x.Id == otherOffer.EntityId).BackedQuantity);

        var unidentifiedCost = await Deliver(empty.Revision, "Unknown cost", 101, null);
        var many = await Place(unidentifiedCost.Revision, unidentifiedCost, bulk, 101);
        var frontier = many.Revision;
        for (var price = 1; price <= 100; price++)
            frontier = (await Price(Offer(frontier, price, new BackingRow(many.EntityId, 1)))).Revision;

        // Retain a v3-shaped original command with no audit evidence. It can be read but cannot be withdrawn.
        var unverified = Guid.NewGuid();
        var originalCommand = Guid.NewGuid();
        await using (var sql = await db.OpenConnectionAsync(token))
        await using (var tx = await sql.BeginTransactionAsync(token))
        {
            async Task Execute(string statement, params object[] parameters)
            {
                await using var sqlCommand = Database.Query(sql, tx, statement, parameters);
                Assert.Equal(1, await sqlCommand.ExecuteNonQueryAsync(token));
            }
            await Execute("UPDATE brickvault.purchases SET revision=revision+1 WHERE id=$1 AND revision=$2",
                purchase.EntityId, frontier);
            await Execute("""
                INSERT INTO brickvault.offers(id,command_id,purchase_id,catalog_id,catalog_revision,unit_price_minor,currency)
                VALUES ($1,$2,$3,$4,$5,101,'USD')
                """, unverified, originalCommand, purchase.EntityId, item.EntityId, item.Revision);
            await Execute("""
                INSERT INTO brickvault.offer_backing(purchase_id,offer_id,placement_id,catalog_id,catalog_revision,quantity)
                VALUES ($1,$2,$3,$4,$5,1)
                """, purchase.EntityId, unverified, many.EntityId, item.EntityId, item.Revision);
            await Execute("""
                INSERT INTO brickvault.commands(command_id,kind,digest,entity_id,revision,correlation_id)
                VALUES ($1,'offer',$2,$3,$4,$5)
                """, originalCommand, new byte[32], unverified, frontier + 1, Guid.NewGuid());
            await tx.CommitAsync(token);
        }
        var pageOne = await drafts.Snapshot(many.EntityId, null, 100, token);
        Assert.Null(pageOne.Position.Layer.ReceiptItemCostMinor);
        Assert.Equal(101, pageOne.OfferBackedQuantity);
        Assert.Equal(100, pageOne.Offers.Count);
        Assert.NotNull(pageOne.NextAfter);
        var pageTwo = await drafts.Snapshot(many.EntityId, pageOne.NextAfter, 100, token);
        Assert.Null(pageTwo.NextAfter);
        Assert.Equal(101, pageOne.Offers.Concat(pageTwo.Offers).Select(x => x.OfferId).Distinct().Count());
        Assert.Equal("invalid_draft_backing_cursor",
            await Reject(() => drafts.Snapshot(origin.EntityId, pageOne.NextAfter, 100, token)));
        var unaudited = await drafts.History(many.EntityId, unverified, null, 1, token);
        Assert.Equal(("unverified-origin", false), (unaudited.Offer.Authority, unaudited.Offer.CanWithdraw));
        Assert.Null(Assert.Single(unaudited.Contributions).RecordedAt);
        Assert.Equal("draft_backing_authority_unverified",
            await Reject(() => Save(Prepare(frontier + 1, many.EntityId, unverified, bulk))));
    }
}
