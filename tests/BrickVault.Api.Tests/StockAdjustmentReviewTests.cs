using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

[Collection("Build holds")]
public sealed class StockAdjustmentReviewTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task ReviewUsesTheLatestWitnessAndPositionScopedCommitmentsWithoutChangingLedger()
    {
        var cs = Environment.GetEnvironmentVariable("BV_TEST_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "BV_TEST_DB with a dedicated bv_m2_test_ database is required.");
        var config = new NpgsqlConnectionStringBuilder(cs);
        Assert.True(config.Host is "127.0.0.1" or "::1" && config.Username == "bv_api" &&
            config.Database?.StartsWith("bv_m2_test_", StringComparison.Ordinal) == true);
        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(cs!);
        var references = new References(db);
        var receiving = new Receiving(db);
        var counts = new StockCounts(db);
        var holds = new BuildHolds(db);
        var adjustments = new StockAdjustmentReviews(db);
        var tag = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var item = await references.Save(new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = tag,
            Variant = null, Name = "Fictional loss review item", ColorId = 0, ColorState = "known",
            Condition = "new", Completeness = "complete", Source = "Fictional reference", License = "operator-owned",
            Mapping = "confirmed"
        }, Guid.NewGuid(), token);
        async Task<(Guid Id, string Code)> Register(string name)
        {
            var code = "LOSS-" + name + "-" + tag;
            var bin = await references.Save(new LocationCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code, Name = "Fictional bin"
            }, Guid.NewGuid(), token);
            return (bin.EntityId, code);
        }
        var a = await Register("A");
        var b = await Register("B");
        var purchase = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "LOSS-" + tag, Source = "Fictional supplier",
            Currency = "USD", OrderedQuantity = 12, PendingChargeMinor = null
        }, Guid.NewGuid(), token);
        Task<Receipt> Deliver(int revision, string name, int quantity, int? cost) => receiving.Save(new DeliveryCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            Reference = name, Quantity = quantity, CatalogId = item.EntityId, ItemCostMinor = cost
        }, Guid.NewGuid(), token);
        Task<Receipt> Place(Receipt delivery, (Guid Id, string Code) bin, int quantity) => receiving.Save(new PutawayCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = delivery.Revision, PurchaseId = purchase.EntityId,
            DeliveryId = delivery.EntityId, CatalogId = item.EntityId, LocationId = bin.Id,
            ConfirmedLocationCode = bin.Code, Quantity = quantity
        }, Guid.NewGuid(), token);
        var known = await Deliver(purchase.Revision, "Documented zero", 10, 0);
        var selected = await Place(known, a, 10);
        var unknown = await Deliver(selected.Revision, "Unknown cost", 2, null);
        var sibling = await Place(unknown, b, 2);
        Task<Receipt> Back(int revision, int price) => receiving.Save(new OfferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            CatalogId = item.EntityId, UnitPriceMinor = price, Backing = [new(selected.EntityId, 3), new(sibling.EntityId, 1)]
        }, Guid.NewGuid(), token);
        var offerA = await Back(sibling.Revision, 125);
        var offerB = await Back(offerA.Revision, 250);
        Task<Receipt> Hold(int revision, string purpose, int quantity) => holds.Save(new StockHoldCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            PositionId = selected.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code,
            Quantity = quantity, Purpose = purpose, Reason = "Project reservation"
        }, Guid.NewGuid(), token);
        var releasedHold = await Hold(offerB.Revision, "Fictional bridge", 3);
        var secondHold = await Hold(releasedHold.Revision, "Fictional tower", 1);

        Task<Receipt> Witness(int revision, (Guid Id, string Code) bin, Guid position, int quantity) =>
            counts.Save(new StockCountCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
                PositionId = position, LocationId = bin.Id, ConfirmedLocationCode = bin.Code,
                CountedQuantity = quantity, Reason = "Witnessed fixture recount"
            }, Guid.NewGuid(), token);
        var count = await Witness(secondHold.Revision, a, selected.EntityId, 5);
        var otherCount = await Witness(count.Revision, b, sibling.EntityId, 0);
        var release = await holds.Save(new StockHoldReleaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = otherCount.Revision, PurchaseId = purchase.EntityId,
            HoldId = releasedHold.EntityId, Quantity = 2, Reason = "Owner selected the bridge hold",
            ShortageId = count.EntityId, ExpectedObservationId = count.EntityId
        }, Guid.NewGuid(), token);

        Task<StockAdjustmentReview> Read(string? after = null, int limit = 25) =>
            adjustments.Read(a.Id, selected.EntityId, count.EntityId, after, limit, token);
        async Task<string> Failure(Func<Task> read) => (await Assert.ThrowsAsync<Fault>(read)).Code;

        var first = await Read(limit: 1);
        Assert.True(first.Complete);
        Assert.Equal("stock-adjustment-review", first.Scope);
        Assert.Equal((a.Id, a.Code, count.EntityId, selected.EntityId, purchase.EntityId, release.Revision),
            (first.Location.Id, first.Location.Code, first.CaseId, first.PositionId, first.PurchaseId, first.PurchaseRevision));
        Assert.Equal((count.EntityId, count.CommandId, count.CorrelationId, a.Code, 5, false),
            (first.LatestObservation.Id, first.LatestObservation.CommandId, first.LatestObservation.CorrelationId,
                first.LatestObservation.LocationCode, first.LatestObservation.CountedQuantity, first.LatestObservation.PostedAdjustment));
        Assert.Equal(new AdjustmentReceiptEvidence(known.EntityId, "Documented zero", 10, 0, "USD", selected.EntityId,
            item.EntityId, item.Revision), first.Receipt);
        Assert.Equal((10, 5, 5, 2, 0, 6, 2, 3, 3),
            (first.LedgerQuantity, first.CandidateLossQuantity, first.PostLossLedgerQuantity, first.ProtectedProjectQuantity,
                first.ProtectedProjectShortfall, first.OfferBackedQuantity, first.OfferCount, first.OfferCapacityAfterLoss,
                first.MinimumOfferBackingReduction));
        Assert.NotNull(first.NextAfter);
        var second = await Read(first.NextAfter, 1);
        Assert.Null(second.NextAfter);
        Assert.Equal(first with { AsOf = second.AsOf, Offers = second.Offers, NextAfter = null }, second);
        var allOffers = first.Offers.Concat(second.Offers).ToDictionary(o => o.Id);
        Assert.Equal(new AdjustmentOfferBacking(offerA.EntityId, 3, 125, "USD"), allOffers[offerA.EntityId]);
        Assert.Equal(new AdjustmentOfferBacking(offerB.EntityId, 3, 250, "USD"), allOffers[offerB.EntityId]);

        var other = await adjustments.Read(b.Id, sibling.EntityId, otherCount.EntityId, null, 25, token);
        Assert.Null(other.Receipt.ItemCostMinor);
        Assert.Equal(unknown.EntityId, other.Receipt.DeliveryId);
        Assert.Equal(2, other.OfferBackedQuantity);
        Assert.Equal(2, other.MinimumOfferBackingReduction);
        Assert.Equal("invalid_adjustment_cursor", await Failure(() =>
            adjustments.Read(b.Id, sibling.EntityId, otherCount.EntityId, first.NextAfter, 1, token)));
        Assert.Equal("invalid_adjustment_cursor", await Failure(() => Read(first.NextAfter![..^32] + Guid.NewGuid().ToString("N"))));
        Assert.Equal("invalid_adjustment_page", await Failure(() => Read(limit: 101)));
        Assert.Equal("unknown_adjustment_position", await Failure(() =>
            adjustments.Read(b.Id, selected.EntityId, count.EntityId, null, 25, token)));
        Assert.Equal("unknown_open_shortage", await Failure(() =>
            adjustments.Read(a.Id, selected.EntityId, otherCount.EntityId, null, 25, token)));
        Assert.Equal("unknown_open_shortage", await Failure(() =>
            adjustments.Read(a.Id, selected.EntityId, Guid.NewGuid(), null, 25, token)));

        var revised = await Witness(release.Revision, a, selected.EntityId, 1);
        Assert.Equal("stale_adjustment_page", await Failure(() => Read(first.NextAfter, 1)));
        var tight = await Read();
        Assert.Equal((revised.EntityId, 9, 1, 0, 6), (tight.LatestObservation.Id, tight.CandidateLossQuantity,
            tight.ProtectedProjectShortfall, tight.OfferCapacityAfterLoss, tight.MinimumOfferBackingReduction));
        var equal = await Witness(revised.Revision, a, selected.EntityId, 10);
        var equalReview = await Read();
        Assert.Equal((equal.EntityId, 0, 10, 0), (equalReview.LatestObservation.Id, equalReview.CandidateLossQuantity,
            equalReview.PostLossLedgerQuantity, equalReview.MinimumOfferBackingReduction));
        var surplus = await Witness(equal.Revision, a, selected.EntityId, 11);
        var surplusReview = await Read();
        Assert.Equal((surplus.EntityId, 1, 0, 10, 0), (surplusReview.LatestObservation.Id,
            surplusReview.LatestObservation.UnidentifiedSurplusQuantity, surplusReview.CandidateLossQuantity,
            surplusReview.PostLossLedgerQuantity, surplusReview.MinimumOfferBackingReduction));
        Assert.Equal(count.EntityId, surplusReview.CaseId);
        var row = Assert.Single((await receiving.CurrentSnapshot(true, token)).Purchases, p => p.Id == purchase.EntityId);
        Assert.Equal((surplus.Revision, 12, 2, 8), (row.Revision, row.Balances.PhysicalOnHand, row.Balances.Reserved,
            row.Balances.BackedQuantity));

        // Leave no active fixture reservations to affect retained-client snapshots in other tests.
        var cleanup = new StockHoldReleaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = surplus.Revision, PurchaseId = purchase.EntityId,
            HoldId = releasedHold.EntityId, Quantity = 1, Reason = "Close the fictional project reservation",
            ShortageId = count.EntityId, ExpectedObservationId = surplus.EntityId
        };
        var firstCleanup = await holds.Save(cleanup, Guid.NewGuid(), token);
        await holds.Save(cleanup with { CommandId = Guid.NewGuid(), ExpectedRevision = firstCleanup.Revision,
            HoldId = secondHold.EntityId }, Guid.NewGuid(), token);
    }
}
