using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

[Collection("Build holds")]
public sealed class StockPositionTimelineTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task RetainedFactsPageByPurchaseRevisionAndDepletedPositionsKeepTheirEvidence()
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
        var transfers = new Transfers(db);
        var counts = new StockCounts(db);
        var holds = new BuildHolds(db);
        var losses = new StockLosses(db);
        var timelines = new StockPositionTimelines(db);
        var stock = new StockRegister(db);
        var tag = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var catalog = await references.Save(new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = tag, Variant = null,
            Name = "Timeline fixture", ColorId = 0, ColorState = "known", Condition = "new", Completeness = "complete",
            Source = "Fictional reference", License = "operator-owned", Mapping = "confirmed"
        }, Guid.NewGuid(), token);
        Task<Receipt> Register(string code) => references.Save(new LocationCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code, Name = "Fictional bin"
        }, Guid.NewGuid(), token);
        var aCode = "TLA-" + tag;
        var bCode = "TLB-" + tag;
        var a = await Register(aCode);
        var b = await Register(bCode);
        var purchase = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "TIMELINE-" + tag, Source = "Fictional purchase",
            Currency = "USD", OrderedQuantity = 7, PendingChargeMinor = null
        }, Guid.NewGuid(), token);
        var delivery = await receiving.Save(new DeliveryCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = purchase.Revision, PurchaseId = purchase.EntityId,
            Reference = "Fixture carton", Quantity = 7, CatalogId = catalog.EntityId, ItemCostMinor = 3
        }, Guid.NewGuid(), token);
        var placed = await receiving.Save(new PutawayCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = delivery.Revision, PurchaseId = purchase.EntityId,
            DeliveryId = delivery.EntityId, CatalogId = catalog.EntityId, LocationId = a.EntityId,
            ConfirmedLocationCode = aCode, Quantity = 7
        }, Guid.NewGuid(), token);
        var offer = await receiving.Save(new OfferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = placed.Revision, PurchaseId = purchase.EntityId,
            CatalogId = catalog.EntityId, UnitPriceMinor = 102, Backing = [new(placed.EntityId, 4)]
        }, Guid.NewGuid(), token);
        var move = new TransferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = offer.Revision, PurchaseId = purchase.EntityId,
            SourcePlacementId = placed.EntityId, SourceLocationId = a.EntityId, ConfirmedSourceLocationCode = aCode,
            DestinationLocationId = b.EntityId, ConfirmedDestinationLocationCode = bCode, Quantity = 2,
            Reason = "Fictional transfer", Backing = [new(offer.EntityId, 1)]
        };
        var firstMove = await transfers.Save(move, Guid.NewGuid(), token);
        var held = await holds.Save(new StockHoldCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = firstMove.Revision, PurchaseId = purchase.EntityId,
            PositionId = placed.EntityId, LocationId = a.EntityId, ConfirmedLocationCode = aCode,
            Quantity = 1, Purpose = "Fixture bridge", Reason = "Reserve for the bridge"
        }, Guid.NewGuid(), token);
        var countCommand = new StockCountCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = held.Revision, PurchaseId = purchase.EntityId,
            PositionId = placed.EntityId, LocationId = a.EntityId, ConfirmedLocationCode = aCode,
            CountedQuantity = 2, Reason = "Witnessed two available pieces"
        };
        var witnessed = await counts.Save(countCommand, Guid.NewGuid(), token);
        var recounted = await counts.Save(countCommand with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = witnessed.Revision, Reason = "Independent short recount"
        }, Guid.NewGuid(), token);
        var released = await holds.Save(new StockHoldReleaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = recounted.Revision, PurchaseId = purchase.EntityId,
            HoldId = held.EntityId, Quantity = 1, Reason = "Owner chose the bridge unit",
            ShortageId = witnessed.EntityId, ExpectedObservationId = recounted.EntityId
        }, Guid.NewGuid(), token);
        var loss = await losses.Save(new StockLossCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = released.Revision, PurchaseId = purchase.EntityId,
            PositionId = placed.EntityId, LocationId = a.EntityId, ConfirmedLocationCode = aCode,
            CaseId = witnessed.EntityId, ExpectedObservationId = recounted.EntityId,
            Reason = "Fictional reviewed loss", OfferReductions = [new(offer.EntityId, 1)]
        }, Guid.NewGuid(), token);
        var afterResolution = await counts.Save(countCommand with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = loss.Revision, Reason = "Equal recount after resolution"
        }, Guid.NewGuid(), token);
        var earlyPage = await timelines.Read(placed.EntityId, null, 1, token);
        Assert.Equal(afterResolution.EntityId, Assert.Single(earlyPage.Events).Id);
        Assert.NotNull(earlyPage.NextAfter);
        Assert.Equal((2, 2, 0, 0), (earlyPage.Position.Layer.Quantity, earlyPage.Position.Layer.BackedQuantity,
            earlyPage.Position.Layer.UnbackedQuantity, earlyPage.Position.Layer.FreeToBackQuantity));
        var transitionRead = timelines.Read(placed.EntityId, null, 25, token);
        var lastMove = await transfers.Save(move with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = afterResolution.Revision, MoveAllBacking = true, Backing = []
        }, Guid.NewGuid(), token);
        var transition = await transitionRead;
        Assert.Equal(transition.Events.Sum(e => e.PhysicalDelta), transition.Position.Layer.Quantity);
        Assert.Equal(transition.Events.Sum(e => e.OfferBackedDelta), transition.Position.Layer.BackedQuantity);
        var newCode = aCode + "-R";
        await references.Save(new LocationCommand
        {
            CommandId = Guid.NewGuid(), Id = a.EntityId, ExpectedRevision = a.Revision, Name = "Renamed fixture bin", Code = newCode
        }, Guid.NewGuid(), token);

        var full = await timelines.Read(placed.EntityId, null, 25, token);
        Assert.True(full.Complete);
        Assert.False(full.HasUndatedEvents);
        Assert.Equal((placed.EntityId, purchase.EntityId, placed.EntityId, delivery.EntityId, newCode),
            (full.PositionId, full.PurchaseId, full.OriginPlacementId, full.DeliveryId, full.Location.Code));
        Assert.Equal((full.PositionId, full.PurchaseId, full.OriginPlacementId, full.DeliveryId, full.Location),
            (full.Position.Layer.PlacementId, full.Position.Layer.PurchaseId, full.Position.OriginPlacementId,
                full.Position.Layer.DeliveryId, full.Position.Location));
        Assert.Equal((0, 0, 0, 0), (full.Position.Layer.Quantity, full.Position.Layer.BackedQuantity,
            full.Position.Layer.UnbackedQuantity, full.Position.Layer.FreeToBackQuantity));
        Assert.Null(full.NextAfter);
        var events = full.Events;
        Assert.Equal(new[] { "transfer-out", "count", "stock-loss", "hold-release", "count", "count", "hold", "transfer-out", "offer-backing", "putaway" },
            events.Select(row => row.Kind));
        Assert.Equal(new[] { lastMove.Revision, afterResolution.Revision, loss.Revision, released.Revision, recounted.Revision, witnessed.Revision, held.Revision,
            firstMove.Revision, offer.Revision, placed.Revision }, events.Select(row => row.PurchaseRevision));
        Assert.Equal(new[] { -2, 0, -3, 0, 0, 0, 0, -2, 0, 7 }, events.Select(row => row.PhysicalDelta));
        Assert.Equal(new[] { 0, 0, 0, -1, 0, 0, 1, 0, 0, 0 }, events.Select(row => row.ReservedDelta));
        Assert.Equal(new[] { -2, 0, -1, 0, 0, 0, 0, -1, 4, 0 }, events.Select(row => row.OfferBackedDelta));
        Assert.All(events, row => Assert.Equal(a.EntityId, row.LocationId));
        Assert.All(events, row => Assert.NotNull(row.OccurredAt));
        Assert.Equal(aCode, events[^1].LocationCodeAtEvent);
        Assert.Null(events[^2].LocationCodeAtEvent);
        Assert.Null(events[3].LocationCodeAtEvent);
        Assert.Equal(("Fixture bridge", held.EntityId), (events[3].HoldPurpose, events[3].HoldId));
        Assert.Equal(("Fixture bridge", held.EntityId), (events[6].HoldPurpose, events[6].HoldId));
        Assert.Equal((witnessed.EntityId, witnessed.EntityId, 2, 5), (events[5].ObservationId, events[5].CaseId,
            events[5].Quantity, events[5].LedgerQuantityAtWitness));
        Assert.Equal((recounted.EntityId, witnessed.EntityId), (events[4].ObservationId, events[4].CaseId));
        Assert.Equal(afterResolution.EntityId, events[1].ObservationId);
        Assert.Null(events[1].CaseId);
        var count = Assert.Single((await counts.Snapshot(a.EntityId, token, observationId: witnessed.EntityId)).Observations);
        Assert.Equal(count.ObservedAt, events[5].OccurredAt);
        var exactLoss = await losses.Read(a.EntityId, placed.EntityId, null, null, events[2].PostingId, null, null, 25, token);
        Assert.Equal((exactLoss.PostingId, exactLoss.CommandId, exactLoss.CorrelationId, exactLoss.PostedAt),
            (events[2].Id, events[2].CommandId, events[2].CorrelationId, events[2].OccurredAt));
        Assert.Equal(recounted.EntityId, events[2].ObservationId);

        var paged = new List<StockPositionTimelineEvent>();
        string? cursor = null;
        do
        {
            var page = await timelines.Read(placed.EntityId, cursor, 2, token);
            Assert.InRange(page.Events.Count, 1, 2);
            Assert.Equal(full.Position, page.Position);
            paged.AddRange(page.Events);
            cursor = page.NextAfter;
        } while (cursor is not null);
        Assert.Equal(events, paged);
        Assert.Equal(events.Skip(2), (await timelines.Read(placed.EntityId, earlyPage.NextAfter, 100, token)).Events);
        var one = await timelines.Read(placed.EntityId, null, 1, token);
        Assert.NotNull(one.NextAfter);
        var sibling = await timelines.Read(lastMove.EntityId, null, 25, token);
        var inbound = Assert.Single(sibling.Events);
        Assert.Equal("transfer-in", inbound.Kind);
        Assert.Equal((lastMove.EntityId, lastMove.CommandId, events[0].OccurredAt, 2, 2, placed.EntityId,
            a.EntityId, aCode), (inbound.Id, inbound.CommandId, inbound.OccurredAt, inbound.PhysicalDelta,
            inbound.OfferBackedDelta, inbound.RelatedPositionId, inbound.RelatedLocationId, inbound.RelatedLocationCode));
        Assert.Equal(placed.EntityId, sibling.OriginPlacementId);
        Assert.Equal((2, 2), (sibling.Position.Layer.Quantity, sibling.Position.Layer.BackedQuantity));
        Assert.Equal(events[0].RelatedPositionId, sibling.PositionId);
        Assert.Equal(b.EntityId, events[0].RelatedLocationId);
        Assert.Equal(bCode, events[0].RelatedLocationCode);
        Assert.Equal(2, (await stock.History(placed.EntityId, null, 25, token)).Rows.Count);
        Assert.Equal(full.Position, Assert.Single((await stock.Search(null, null, null, placed.EntityId, null, 25, token)).Rows));

        async Task<string> Reject(Func<Task> read) => (await Assert.ThrowsAsync<Fault>(read)).Code;
        Assert.Equal("invalid_timeline_cursor", await Reject(() => timelines.Read(lastMove.EntityId, one.NextAfter, 25, token)));
        Assert.Equal("invalid_timeline_cursor", await Reject(() => timelines.Read(lastMove.EntityId,
            lastMove.EntityId.ToString("N") + one.NextAfter![32..], 25, token)));
        Assert.Equal("invalid_timeline_cursor", await Reject(() => timelines.Read(firstMove.EntityId,
            $"{firstMove.EntityId:N}.stock-loss.{loss.EntityId:N}", 25, token)));
        Assert.Equal("invalid_timeline_page", await Reject(() => timelines.Read(placed.EntityId, null, 101, token)));
        Assert.Equal("unknown_position", await Reject(() => timelines.Read(Guid.NewGuid(), null, 25, token)));
    }
}
