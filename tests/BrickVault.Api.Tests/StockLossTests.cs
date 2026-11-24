using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

[Collection("Build holds")]
public sealed class StockLossTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task LossAndNamedReductionsReconcileOnlyTheSelectedPositionAndPreserveReceiptBasis()
    {
        var cs = Environment.GetEnvironmentVariable("BV_TEST_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "BV_TEST_DB with a dedicated bv_m2_test_ database is required.");
        var settings = new NpgsqlConnectionStringBuilder(cs);
        Assert.True(settings.Host is "127.0.0.1" or "::1" && settings.Username == "bv_api" &&
            settings.Database?.StartsWith("bv_m2_test_", StringComparison.Ordinal) == true);
        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(cs!);
        var references = new References(db);
        var receiving = new Receiving(db);
        var counts = new StockCounts(db);
        var holds = new BuildHolds(db);
        var losses = new StockLosses(db);
        var bins = new BinStock(db);
        var inventory = new StockRegister(db);
        var transfers = new Transfers(db);
        var tag = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var item = await references.Save(new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = tag,
            Variant = null, Name = "Fictional stock loss fixture", ColorId = 0, ColorState = "known",
            Condition = "new", Completeness = "complete", Source = "Fictional reference", License = "operator-owned",
            Mapping = "confirmed"
        }, Guid.NewGuid(), token);
        async Task<(Guid Id, string Code)> Register(string name)
        {
            var code = "POSTLOSS-" + name + "-" + tag;
            var registration = await references.Save(new LocationCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code, Name = "Fictional bin"
            }, Guid.NewGuid(), token);
            return (registration.EntityId, code);
        }
        var a = await Register("A");
        var b = await Register("B");
        var c = await Register("C");
        var d = await Register("D");
        var purchase = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "POSTLOSS-" + tag, Source = "Fictional supplier",
            Currency = "USD", OrderedQuantity = 13, PendingChargeMinor = 7
        }, Guid.NewGuid(), token);
        Task<Receipt> Deliver(int revision, string name, int quantity, int? cost) => receiving.Save(new DeliveryCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            Reference = name, Quantity = quantity, CatalogId = item.EntityId, ItemCostMinor = cost
        }, Guid.NewGuid(), token);
        Task<Receipt> Place(int revision, Receipt delivery, (Guid Id, string Code) bin, int quantity) => receiving.Save(new PutawayCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            DeliveryId = delivery.EntityId, CatalogId = item.EntityId, LocationId = bin.Id,
            ConfirmedLocationCode = bin.Code, Quantity = quantity
        }, Guid.NewGuid(), token);
        var known = await Deliver(purchase.Revision, "Documented USD 1.03", 10, 103);
        var origin = await Place(known.Revision, known, a, 9);
        var move = new TransferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = origin.Revision, PurchaseId = purchase.EntityId,
            SourcePlacementId = origin.EntityId, SourceLocationId = a.Id, ConfirmedSourceLocationCode = a.Code,
            DestinationLocationId = b.Id, ConfirmedDestinationLocationCode = b.Code, Quantity = 2,
            Reason = "Move two pieces without changing original cost", Backing = []
        };
        var sibling = await transfers.Save(move, Guid.NewGuid(), token);
        var unknown = await Deliver(sibling.Revision, "Unknown cost", 2, null);
        var unknownPosition = await Place(unknown.Revision, unknown, c, 2);
        var zero = await Deliver(unknownPosition.Revision, "Documented zero", 1, 0);
        var zeroPosition = await Place(zero.Revision, zero, d, 1);
        Task<Receipt> Offer(int revision, int price, BackingRow[] backing) => receiving.Save(new OfferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            CatalogId = item.EntityId, UnitPriceMinor = price, Backing = backing
        }, Guid.NewGuid(), token);
        var offerA = await Offer(zeroPosition.Revision, 249, [new(origin.EntityId, 3), new(sibling.EntityId, 1)]);
        var offerB = await Offer(offerA.Revision, 399, [new(origin.EntityId, 2), new(sibling.EntityId, 1)]);
        var foreignOffer = await Offer(offerB.Revision, 510, [new(unknownPosition.EntityId, 2)]);
        Task<Receipt> Hold(int revision, string purpose) => holds.Save(new StockHoldCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId, PositionId = origin.EntityId,
            LocationId = a.Id, ConfirmedLocationCode = a.Code, Quantity = 1, Purpose = purpose, Reason = "Retain one unit for project"
        }, Guid.NewGuid(), token);
        var firstHold = await Hold(foreignOffer.Revision, "Test bridge");
        var secondHold = await Hold(firstHold.Revision, "Test tower");
        Task<Receipt> Witness(int revision, Guid position, (Guid Id, string Code) bin, int quantity) =>
            counts.Save(new StockCountCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
                PositionId = position, LocationId = bin.Id, ConfirmedLocationCode = bin.Code,
                CountedQuantity = quantity, Reason = "Witnessed fixture recount"
            }, Guid.NewGuid(), token);
        var firstCount = await Witness(secondHold.Revision, origin.EntityId, a, 1);
        StockLossCommand Prepare(Receipt witness, Guid caseId, Guid position, (Guid Id, string Code) bin,
            TransferBacking[] reductions) => new()
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = witness.Revision, PurchaseId = purchase.EntityId,
            PositionId = position, LocationId = bin.Id, ConfirmedLocationCode = bin.Code, CaseId = caseId,
            ExpectedObservationId = witness.EntityId, Reason = "Post reviewed missing pieces", OfferReductions = reductions
        };
        Task<Receipt> Save(StockLossCommand command) => losses.Save(command, Guid.NewGuid(), token);
        async Task<string> Reject(Func<Task> action) => (await Assert.ThrowsAsync<Fault>(action)).Code;
        Assert.Equal("stock_loss_protected_shortfall", await Reject(() => Save(
            Prepare(firstCount, firstCount.EntityId, origin.EntityId, a, []))));
        var equal = await Witness(firstCount.Revision, origin.EntityId, a, 7);
        Assert.Equal("stock_loss_no_loss", await Reject(() => Save(Prepare(equal, firstCount.EntityId, origin.EntityId, a, []))));
        var latest = await Witness(equal.Revision, origin.EntityId, a, 4);
        var release = await holds.Save(new StockHoldReleaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = latest.Revision, PurchaseId = purchase.EntityId, HoldId = firstHold.EntityId,
            Quantity = 1, Reason = "Owner released bridge unit only", ShortageId = firstCount.EntityId, ExpectedObservationId = latest.EntityId
        }, Guid.NewGuid(), token);
        var command = Prepare(latest, firstCount.EntityId, origin.EntityId, a, [new(offerA.EntityId, 1), new(offerB.EntityId, 1)])
            with { ExpectedRevision = release.Revision };
        Assert.Equal("revision_conflict", await Reject(() => Save(command with { ExpectedRevision = latest.Revision })));
        Assert.Equal("stock_loss_bin_confirmation_mismatch", await Reject(() => Save(command with { ConfirmedLocationCode = b.Code })));
        Assert.Equal("stock_loss_position_mismatch", await Reject(() => Save(command with { LocationId = b.Id })));
        Assert.Equal("stock_loss_observation_conflict", await Reject(() => Save(command with { ExpectedObservationId = firstCount.EntityId })));
        Assert.Equal("stock_loss_case_conflict", await Reject(() => Save(command with { CaseId = Guid.NewGuid() })));
        Assert.Equal("invalid_stock_loss", await Reject(() => Save(command with
        {
            OfferReductions = [new(offerA.EntityId, 1), new(offerA.EntityId, 1)]
        })));
        foreach (var rejected in new TransferBacking[][]
        {
            [], [new(offerA.EntityId, 3)], [new(foreignOffer.EntityId, 2)], [new(Guid.NewGuid(), 2)]
        })
            Assert.Equal("stock_loss_offer_reduction_mismatch", await Reject(() => Save(command with { OfferReductions = rejected })));

        Task<StockLossSnapshot> Read(Guid location, Guid position, Guid? caseId = null, Guid? observationId = null,
            Guid? postingId = null, Guid? commandId = null, string? after = null, int limit = 25) =>
            losses.Read(location, position, caseId, observationId, postingId, commandId, after, limit, token);
        Assert.Equal("unknown_stock_loss", await Reject(() => Read(a.Id, origin.EntityId, commandId: command.CommandId)));
        Assert.Single((await counts.Snapshot(a.Id, token, origin.EntityId)).Shortages);
        Assert.Equal(7, (await bins.Snapshot(a.Id, token)).PhysicalOnHand);
        Assert.Equal(release.Revision, Assert.Single((await receiving.CurrentSnapshot(true, token)).Purchases,
            p => p.Id == purchase.EntityId).Revision);

        async Task<(StockLossCommand Request, Receipt? Result)> TryPost(StockLossCommand candidate)
        {
            try { return (candidate, await Save(candidate)); }
            catch (Fault e) when (e.Code == "revision_conflict") { return (candidate, null); }
        }
        var rival = command with { CommandId = Guid.NewGuid(), Reason = "Rival owner decision" };
        var raced = await Task.WhenAll(TryPost(command), TryPost(rival));
        var accepted = Assert.Single(raced, x => x.Result is not null);
        var receipt = Assert.IsType<Receipt>(accepted.Result);
        var replays = await Task.WhenAll(Save(accepted.Request), Save(accepted.Request));
        Assert.All(replays, x => Assert.Equal(receipt, x));
        Assert.Equal("command_mismatch", await Reject(() => Save(accepted.Request with { Reason = "Different decision" })));
        Assert.Equal("stock-loss", receipt.Kind);
        Assert.Equal("stock-loss", (await references.Diagnostic(receipt.CorrelationId, token)).Kind);
        Assert.DoesNotContain((await references.Workspace(token, allAudit: true)).Audit, a => a.CommandId == receipt.CommandId);

        var first = await Read(a.Id, origin.EntityId, caseId: firstCount.EntityId, limit: 1);
        Assert.Equal((receipt.EntityId, receipt.CommandId, receipt.CorrelationId, receipt.Revision),
            (first.PostingId, first.CommandId, first.CorrelationId, first.Revision));
        Assert.Equal((firstCount.EntityId, latest.EntityId, latest.CorrelationId, a.Code, false),
            (first.CaseId, first.ReviewedObservation.Id, first.ReviewedObservation.CorrelationId,
                first.ConfirmedLocationCode, first.ReviewedObservation.PostedAdjustment));
        Assert.Equal((7, 4, 3, 1, 5, 3, 0, 30, 73),
            (first.LedgerQuantityBefore, first.LedgerQuantityAfter, first.LostQuantity, first.ProtectedProjectQuantity,
                first.OfferBackedQuantityBefore, first.OfferBackedQuantityAfter, first.PriorDeliveryLossQuantity,
                first.AllocatedItemCostMinor, first.RemainingDeliveryItemCostMinor));
        Assert.NotNull(first.NextAfter);
        var pageTwo = await Read(a.Id, origin.EntityId, postingId: receipt.EntityId, after: first.NextAfter, limit: 1);
        Assert.Null(pageTwo.NextAfter);
        var reductions = first.Reductions.Concat(pageTwo.Reductions).ToDictionary(r => r.OfferId);
        Assert.Equal(new StockLossReduction(offerA.EntityId, 1, 3, 2, 249, "USD"), reductions[offerA.EntityId]);
        Assert.Equal(new StockLossReduction(offerB.EntityId, 1, 2, 1, 399, "USD"), reductions[offerB.EntityId]);
        foreach (var witness in new[] { firstCount, equal, latest })
            Assert.Equal(receipt.EntityId, (await Read(a.Id, origin.EntityId, observationId: witness.EntityId)).PostingId);
        Assert.Equal(receipt.EntityId, (await Read(a.Id, origin.EntityId, commandId: receipt.CommandId)).PostingId);
        Assert.Equal("unknown_stock_loss", await Reject(() => Read(b.Id, sibling.EntityId, postingId: receipt.EntityId)));
        Assert.Equal("invalid_stock_loss_cursor", await Reject(() => Read(a.Id, origin.EntityId,
            postingId: receipt.EntityId, after: Guid.NewGuid().ToString("N") + "." + offerA.EntityId.ToString("N"))));
        Assert.Empty((await counts.Snapshot(a.Id, token, origin.EntityId)).Shortages);
        Assert.Single((await counts.Snapshot(a.Id, token, observationId: equal.EntityId)).Observations);
        Assert.Equal(1, Assert.Single((await holds.Snapshot(a.Id, origin.EntityId, null, 25, token)).Holds,
            h => h.Id == secondHold.EntityId).RemainingQuantity);
        Assert.Equal(4, (await bins.Snapshot(a.Id, token)).PhysicalOnHand);
        Assert.Equal(2, (await bins.Movements(b.Id, token)).BackedQuantity);
        Assert.Equal("receiving_client_upgrade_required", await Reject(() => receiving.CurrentSnapshot(true, token)));
        Assert.Equal("receiving_client_upgrade_required", await Reject(() => receiving.LegacySnapshot(token)));

        var siblingCount = await Witness(receipt.Revision, sibling.EntityId, b, 0);
        var secondLoss = await Save(Prepare(siblingCount, siblingCount.EntityId, sibling.EntityId, b,
            [new(offerA.EntityId, 1), new(offerB.EntityId, 1)]));
        var siblingLoss = await Read(b.Id, sibling.EntityId, commandId: secondLoss.CommandId);
        Assert.Equal((0, 2, 3, 21, 52, origin.EntityId), (siblingLoss.LedgerQuantityAfter, siblingLoss.LostQuantity,
            siblingLoss.PriorDeliveryLossQuantity, siblingLoss.AllocatedItemCostMinor, siblingLoss.RemainingDeliveryItemCostMinor,
            siblingLoss.Receipt.OriginPlacementId));
        Assert.Empty((await bins.Snapshot(b.Id, token)).Layers);
        var emptyExact = await inventory.Search(null, null, null, sibling.EntityId, null, 25, token);
        Assert.Equal(0, Assert.Single(emptyExact.Rows).Layer.Quantity);
        Assert.Equal("invalid_stock_loss_cursor", await Reject(() => Read(b.Id, sibling.EntityId,
            postingId: secondLoss.EntityId, after: first.NextAfter)));

        var unknownCount = await Witness(secondLoss.Revision, unknownPosition.EntityId, c, 0);
        var unknownLoss = await Save(Prepare(unknownCount, unknownCount.EntityId, unknownPosition.EntityId, c,
            [new(foreignOffer.EntityId, 2)]));
        var unknownBasis = await Read(c.Id, unknownPosition.EntityId, postingId: unknownLoss.EntityId);
        Assert.Null(unknownBasis.AllocatedItemCostMinor);
        Assert.Null(unknownBasis.RemainingDeliveryItemCostMinor);
        var zeroCount = await Witness(unknownLoss.Revision, zeroPosition.EntityId, d, 0);
        var zeroLoss = await Save(Prepare(zeroCount, zeroCount.EntityId, zeroPosition.EntityId, d, []));
        var zeroBasis = await Read(d.Id, zeroPosition.EntityId, postingId: zeroLoss.EntityId);
        Assert.Equal(0, zeroBasis.AllocatedItemCostMinor);
        Assert.Equal(0, zeroBasis.RemainingDeliveryItemCostMinor);
        Assert.Empty(zeroBasis.Reductions);

        async Task<LossPurchaseRow> ReadPurchase() =>
            Assert.Single((await receiving.LossSnapshot(token)).Purchases, p => p.Id == purchase.EntityId);
        var now = await ReadPurchase();
        Assert.Equal((5, 1, 1, 3, 3, 0, 3, 0, 103, 110), (now.Balances.PhysicalOnHand, now.Balances.Unavailable,
            now.Balances.Reserved, now.Balances.Free, now.Balances.BackedQuantity, now.Balances.WithheldQuantity,
            now.Balances.KnownCostFreeQuantity, now.Balances.UnknownCostFreeQuantity, now.Balances.KnownItemCostMinor,
            now.Balances.KnownPurchaseComponentsMinor));
        var recordedDelivery = Assert.Single(now.Deliveries, d => d.Id == known.EntityId);
        Assert.Equal((10, 103, 1, 5, 51, 52), (recordedDelivery.Quantity, recordedDelivery.ItemCostMinor,
            recordedDelivery.IntakeQuantity, recordedDelivery.LostQuantity, recordedDelivery.AllocatedLostItemCostMinor,
            recordedDelivery.RemainingItemCostMinor));
        var retained = Assert.Single(now.Offers, o => o.Id == foreignOffer.EntityId);
        Assert.Equal((510, 0, false, 0), (retained.UnitPriceMinor, retained.BackedQuantity, retained.BackingCurrent, retained.PublishedQuantity));
        Assert.Empty(retained.Backing);
        Assert.Equal(2, Assert.Single(now.Offers, o => o.Id == offerA.EntityId).BackedQuantity);
        Assert.Equal("quantity_unavailable", await Reject(() => Place(zeroLoss.Revision, known, a, 2)));
        var finalPutaway = await Place(zeroLoss.Revision, known, a, 1);
        Assert.Equal(0, Assert.Single((await ReadPurchase()).Deliveries, row => row.Id == known.EntityId).IntakeQuantity);

        var availableMove = move with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = finalPutaway.Revision, Quantity = 1, Backing = [new(offerA.EntityId, 1)]
        };
        Assert.Equal("reserved_stock_unavailable", await Reject(() => transfers.Save(availableMove with { Quantity = 2 },
            Guid.NewGuid(), token)));
        var moved = await transfers.Save(availableMove, Guid.NewGuid(), token);
        Assert.Equal(1, (await bins.Snapshot(b.Id, token)).PhysicalOnHand);
        Assert.Equal(1, (await bins.Snapshot(b.Id, token)).BackedQuantity);
        var readAgain = await Read(a.Id, origin.EntityId, commandId: receipt.CommandId, limit: 1);
        Assert.Equal(first with { AsOf = readAgain.AsOf, Reductions = readAgain.Reductions }, readAgain);
        Assert.Equal(Assert.Single(first.Reductions), Assert.Single(readAgain.Reductions));
        Assert.Equal(receipt, await Save(accepted.Request));

        // Other fixture tests share this isolated database. Release the remaining fictional project hold.
        await holds.Save(new StockHoldReleaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = moved.Revision, PurchaseId = purchase.EntityId,
            HoldId = secondHold.EntityId, Quantity = 1, Reason = "Close the fictional project reservation"
        }, Guid.NewGuid(), token);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var grants = Database.Query(connection, null, """
            SELECT NOT has_table_privilege('brickvault.stock_loss_postings','UPDATE,DELETE')
              AND NOT has_table_privilege('brickvault.stock_loss_offer_reductions','UPDATE,DELETE')
              AND NOT has_column_privilege('brickvault.stock_loss_offer_reductions','prior_backed_quantity','INSERT')
              AND NOT has_column_privilege('brickvault.stock_shortage_cases','resolved_by_command_id','UPDATE')
              AND NOT has_table_privilege('bv_worker','brickvault.stock_loss_postings','INSERT,UPDATE,DELETE')
            """);
        Assert.Equal(true, await grants.ExecuteScalarAsync(token));
    }
}
