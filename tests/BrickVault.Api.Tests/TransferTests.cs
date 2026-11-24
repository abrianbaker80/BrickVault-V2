using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

public sealed class TransferTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task TransferConservesCustodyAndKeepsExplicitOfferBackingAndOriginalCosts()
    {
        var cs = IsolatedDatabase();
        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(cs);
        var refs = new References(db);
        var receiving = new Receiving(db);
        var transfers = new Transfers(db);
        var bins = new BinStock(db);
        var marker = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var catalog = new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = marker,
            Variant = "a", Name = "Fictional layer", ColorId = 0, ColorState = "known", Condition = "new",
            Completeness = "complete", Source = "Transfer fixture", License = "operator-owned", Mapping = "confirmed"
        };
        var item = await refs.Save(catalog, Guid.NewGuid(), token);
        async Task<LocationRow> Register(string code)
        {
            var location = await refs.Save(new LocationCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code + "-" + marker, Name = "Fixture bin"
            }, Guid.NewGuid(), token);
            return new LocationRow(location.EntityId, location.Revision, code + "-" + marker, "Fixture bin");
        }

        var a = await Register("TR-A");
        var b = await Register("TR-B");
        var c = await Register("TR-C");
        var order = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "TR-" + marker, Source = "Fixture invoice",
            Currency = "JPY", OrderedQuantity = 12, PendingChargeMinor = 60
        }, Guid.NewGuid(), token);
        Task<Receipt> Deliver(int revision, string reference, int quantity, int? cost) =>
            receiving.Save(new DeliveryCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = order.EntityId,
                Reference = reference, Quantity = quantity, CatalogId = item.EntityId, ItemCostMinor = cost
            }, Guid.NewGuid(), token);
        Task<Receipt> Place(Receipt delivery, int quantity) =>
            receiving.Save(new PutawayCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = delivery.Revision, PurchaseId = order.EntityId,
                DeliveryId = delivery.EntityId, CatalogId = item.EntityId, LocationId = a.Id,
                ConfirmedLocationCode = a.Code, Quantity = quantity
            }, Guid.NewGuid(), token);
        Task<Receipt> Offer(int revision, Guid placement, int quantity, int price) =>
            receiving.Save(new OfferCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = order.EntityId,
                CatalogId = item.EntityId, UnitPriceMinor = price, Backing = [new(placement, quantity)]
            }, Guid.NewGuid(), token);

        var zero = await Deliver(order.Revision, "Documented zero", 8, 0);
        var origin = await Place(zero, 8);
        var unknown = await Deliver(origin.Revision, "Cost unknown", 4, null);
        var unknownOrigin = await Place(unknown, 4);
        var firstOffer = await Offer(unknownOrigin.Revision, origin.EntityId, 3, 150);
        var secondOffer = await Offer(firstOffer.Revision, origin.EntityId, 2, 220);
        async Task<PurchaseRow> ReadPurchase() =>
            Assert.Single((await receiving.Snapshot(token)).Purchases, p => p.Id == order.EntityId);
        var before = await ReadPurchase();

        TransferCommand Move(Receipt source, int revision, LocationRow from, LocationRow to, int quantity,
            params TransferBacking[] backing) => new()
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = order.EntityId,
            SourcePlacementId = source.EntityId, SourceLocationId = from.Id, ConfirmedSourceLocationCode = from.Code,
            DestinationLocationId = to.Id, ConfirmedDestinationLocationCode = to.Code, Quantity = quantity,
            Reason = "Confirmed reorganization", Backing = backing
        };
        Task<Receipt> Save(TransferCommand x) => transfers.Save(x, Guid.NewGuid(), token);
        var move = Move(origin, secondOffer.Revision, a, b, 4,
            new(firstOffer.EntityId, 2), new(secondOffer.EntityId, 1));
        async Task Reject(TransferCommand command, string code) =>
            Assert.Equal(code, (await Assert.ThrowsAsync<Fault>(() => Save(command))).Code);

        await Reject(move with { ConfirmedSourceLocationCode = c.Code }, "transfer_bin_confirmation_mismatch");
        await Reject(move with { ConfirmedDestinationLocationCode = c.Code }, "transfer_bin_confirmation_mismatch");
        await Reject(move with { SourceLocationId = c.Id, ConfirmedSourceLocationCode = c.Code }, "transfer_source_mismatch");
        await Reject(move with { Quantity = 9 }, "transfer_quantity_unavailable");
        await Reject(move with { Backing = [] }, "transfer_backing_unavailable");
        await Reject(move with { Backing = [new(firstOffer.EntityId, 4)] }, "transfer_backing_unavailable");
        await Reject(move with { Backing = [new(firstOffer.EntityId, 3), new(secondOffer.EntityId, 2)] },
            "transfer_backing_unavailable");
        var unchanged = await ReadPurchase();
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Equal(before.Balances, unchanged.Balances);
        Assert.Empty((await bins.Movements(a.Id, token)).Movements);
        Assert.Empty((await bins.Snapshot(b.Id, token)).Layers);

        var moved = await Save(move);
        Assert.Equal(moved, await Save(move));
        await Reject(move with { Reason = "Changed replay" }, "command_mismatch");
        var split = await ReadPurchase();
        Assert.Equal(before.Balances, split.Balances);
        var known = Assert.Single(split.Deliveries, x => x.Id == zero.EntityId);
        Assert.Equal(0, known.ItemCostMinor);
        Assert.Equal(8, known.Quantity);
        Assert.Equal(0, known.IntakeQuantity);
        Assert.Equal(4, Assert.Single(known.Placements, p => p.Id == origin.EntityId).Quantity);
        Assert.Equal(4, Assert.Single(known.Placements, p => p.Id == moved.EntityId).Quantity);
        var arrival = await bins.Movements(b.Id, token);
        var arrived = Assert.Single(arrival.Positions);
        Assert.Equal((4L, 3L, 1L, 1L), Totals(arrival));
        Assert.Equal(moved.Revision, arrived.PurchaseRevision);
        Assert.Equal(a.Code, arrived.Layer.LocationCodeAtPutaway);
        Assert.Equal(b.Code, arrived.LocationCodeAtPlacement);
        Assert.Equal((3, 1), (arrived.Layer.BackedQuantity, arrived.Layer.UnbackedQuantity));
        Assert.Contains(arrived.Backing, x => x.OfferId == firstOffer.EntityId && x.Quantity == 2 && x.UnitPriceMinor == 150);
        Assert.Contains(arrived.Backing, x => x.OfferId == secondOffer.EntityId && x.Quantity == 1 && x.Currency == "JPY");
        var firstEvent = Assert.Single(arrival.Movements);
        Assert.Equal(moved.CorrelationId, firstEvent.CorrelationId);
        Assert.Equal(origin.EntityId, firstEvent.SourcePlacementId);
        Assert.Equal(moved.EntityId, firstEvent.DestinationPlacementId);
        Assert.Contains((await bins.Movements(a.Id, token)).Movements, x => x.Id == firstEvent.Id);

        var onward = await Save(Move(moved, moved.Revision, b, c, 4,
            new(firstOffer.EntityId, 2), new(secondOffer.EntityId, 1)));
        var emptied = await bins.Movements(b.Id, token);
        Assert.Empty(emptied.Positions);
        Assert.Equal((0L, 0L, 0L, 0L), Totals(emptied));
        Assert.Equal(2, emptied.Movements.Count);
        Assert.DoesNotContain((await ReadPurchase()).Deliveries.SelectMany(d => d.Placements), x => x.Id == moved.EntityId);

        var unknownMoved = await Save(Move(unknownOrigin, onward.Revision, a, c, 2));
        var pending = Assert.Single((await bins.Snapshot(c.Id, token)).Layers, x => x.PlacementId == unknownMoved.EntityId);
        Assert.Null(pending.ReceiptItemCostMinor);
        Assert.Equal(4, pending.ReceiptQuantity);
        Assert.Equal("JPY", pending.Currency);
        // Legacy offer creation accepts an effective position introduced by a transfer.
        var newOffer = await Offer(unknownMoved.Revision, unknownMoved.EntityId, 1, 300);
        var preCorrection = await ReadPurchase();
        Assert.Equal(6, preCorrection.Balances.BackedQuantity);
        Assert.Equal(0, preCorrection.Balances.KnownItemCostMinor);
        Assert.Equal(60, preCorrection.Balances.KnownPurchaseComponentsMinor);
        Assert.Equal(8, preCorrection.Balances.KnownCostFreeQuantity);
        Assert.Equal(4, preCorrection.Balances.UnknownCostFreeQuantity);

        var corrected = await refs.Save(catalog with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = item.Revision, Id = item.EntityId, Condition = "used"
        }, Guid.NewGuid(), token);
        var compete = Move(origin, newOffer.Revision, a, c, 2);
        async Task<Receipt?> TryMove()
        {
            try { return await Save(compete with { CommandId = Guid.NewGuid() }); }
            catch (Fault f) when (f.Code == "revision_conflict") { return null; }
        }
        var winner = Assert.Single((await Task.WhenAll(TryMove(), TryMove())).OfType<Receipt>());
        var stale = Assert.Single((await bins.Movements(c.Id, token)).Positions, x => x.Layer.PlacementId == winner.EntityId);
        Assert.Equal(item.Revision, stale.Layer.CatalogRevision);
        Assert.Equal(corrected.Revision, stale.Layer.CurrentCatalog.Revision);
        Assert.Equal(0, stale.Layer.FreeToBackQuantity);
        Assert.Equal(2, stale.Layer.UnbackedQuantity);
        var after = await ReadPurchase();
        Assert.Equal(preCorrection.Balances, after.Balances);
        Assert.All(after.Offers, x => Assert.False(x.BackingCurrent));
        Assert.Equal((3, 150), (Assert.Single(after.Offers, x => x.Id == firstOffer.EntityId).BackedQuantity,
            Assert.Single(after.Offers, x => x.Id == firstOffer.EntityId).UnitPriceMinor));
        Assert.Equal(2, Assert.Single(after.Offers, x => x.Id == secondOffer.EntityId).BackedQuantity);
        Assert.Equal("transfer", (await refs.Diagnostic(winner.CorrelationId, token)).Kind);
        Assert.DoesNotContain((await refs.Workspace(token, allAudit: true)).Audit, x => x.CommandId == winner.CommandId);
        Assert.Equal(moved, await Save(move));
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task LegacyReadAndFullTransferHandleIndependentBackingBoundaries()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(IsolatedDatabase());
        var refs = new References(db);
        var receiving = new Receiving(db);
        var transfers = new Transfers(db);
        var bins = new BinStock(db);
        var marker = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var catalog = await refs.Save(new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = marker,
            Variant = null, Name = "Backing boundary fixture", ColorId = 0, ColorState = "known", Condition = "new",
            Completeness = "complete", Source = "Fictional transfer fixture", License = "operator-owned", Mapping = "confirmed"
        }, Guid.NewGuid(), token);
        async Task<LocationRow> Register(string code)
        {
            var row = await refs.Save(new LocationCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code + "-" + marker, Name = "Fixture bin"
            }, Guid.NewGuid(), token);
            return new LocationRow(row.EntityId, row.Revision, code + "-" + marker, "Fixture bin");
        }
        var a = await Register("BND-A");
        var b = await Register("BND-B");
        var c = await Register("BND-C");

        async Task<(Guid Purchase, Receipt Position)> Stock(string label, int quantity)
        {
            var purchase = await receiving.Save(new PurchaseCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = label + "-" + marker,
                Source = "Fixture", Currency = "USD", OrderedQuantity = quantity, PendingChargeMinor = null
            }, Guid.NewGuid(), token);
            var delivery = await receiving.Save(new DeliveryCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = purchase.Revision, PurchaseId = purchase.EntityId,
                Reference = "Fixture", Quantity = quantity, CatalogId = catalog.EntityId, ItemCostMinor = 0
            }, Guid.NewGuid(), token);
            var position = await receiving.Save(new PutawayCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = delivery.Revision, PurchaseId = purchase.EntityId,
                DeliveryId = delivery.EntityId, CatalogId = catalog.EntityId, LocationId = a.Id,
                ConfirmedLocationCode = a.Code, Quantity = quantity
            }, Guid.NewGuid(), token);
            return (purchase.EntityId, position);
        }
        Task<Receipt> Offer(Guid purchase, Guid placement, int revision, int quantity) =>
            receiving.Save(new OfferCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase,
                CatalogId = catalog.EntityId, UnitPriceMinor = 100, Backing = [new(placement, quantity)]
            }, Guid.NewGuid(), token);
        TransferCommand Move(Guid purchase, Guid position, int revision, LocationRow from, LocationRow to,
            int quantity, params TransferBacking[] backing) => new()
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase,
            SourcePlacementId = position, SourceLocationId = from.Id, ConfirmedSourceLocationCode = from.Code,
            DestinationLocationId = to.Id, ConfirmedDestinationLocationCode = to.Code, Quantity = quantity,
            Reason = "Fictional backing boundary", Backing = backing
        };
        Task<Receipt> Save(TransferCommand command) => transfers.Save(command, Guid.NewGuid(), token);

        // One offer spreads across 64 positions and then crosses the released read limit.
        var wide = await Stock("WIDE-OFFER", 65);
        var spreadOffer = await Offer(wide.Purchase, wide.Position.EntityId, wide.Position.Revision, 65);
        var revision = spreadOffer.Revision;
        for (var split = 0; split < 63; split++)
            revision = (await Save(Move(wide.Purchase, wide.Position.EntityId, revision, a, b, 1,
                new TransferBacking(spreadOffer.EntityId, 1)))).Revision;

        var original = Assert.Single((await receiving.LegacySnapshot(token)).Purchases, x => x.Id == wide.Purchase);
        var representable = Assert.Single(original.Offers);
        Assert.Equal(64, representable.Backing.Count);
        Assert.Equal(65, representable.BackedQuantity);
        await Save(Move(wide.Purchase, wide.Position.EntityId, revision, a, b, 1,
            new TransferBacking(spreadOffer.EntityId, 1)));

        var legacyFault = await Assert.ThrowsAsync<Fault>(() => receiving.LegacySnapshot(token));
        Assert.Equal((426, "receiving_client_upgrade_required"), (legacyFault.Status, legacyFault.Code));
        var effective = Assert.Single((await receiving.Snapshot(token)).Purchases, x => x.Id == wide.Purchase);
        var uncapped = Assert.Single(effective.Offers);
        Assert.Equal(65, uncapped.Backing.Count);
        Assert.Equal(65, uncapped.Backing.Select(b => b.PlacementId).Distinct().Count());
        Assert.All(uncapped.Backing, line => Assert.Equal(1, line.Quantity));
        Assert.Equal(original.Balances, effective.Balances);

        // A different position backs 65 distinct offers. There is no arbitrary selection limit.
        var dense = await Stock("DENSE-POSITION", 70);
        revision = dense.Position.Revision;
        List<TransferBacking> selected = [];
        for (var i = 0; i < 65; i++)
        {
            var offer = await Offer(dense.Purchase, dense.Position.EntityId, revision, 1);
            selected.Add(new TransferBacking(offer.EntityId, 1));
            revision = offer.Revision;
        }

        var explicitMove = Move(dense.Purchase, dense.Position.EntityId, revision, a, b, 67, [..selected]);
        var mixed = await Assert.ThrowsAsync<Fault>(() => Save(explicitMove with { MoveAllBacking = true }));
        Assert.Equal((400, "invalid_transfer"), (mixed.Status, mixed.Code));
        var partial = await Assert.ThrowsAsync<Fault>(() => Save(explicitMove with { MoveAllBacking = true, Backing = [] }));
        Assert.Equal((409, "transfer_all_backing_requires_full"), (partial.Status, partial.Code));
        var originalSource = Assert.Single((await bins.Movements(a.Id, token)).Positions,
            p => p.Layer.PlacementId == dense.Position.EntityId);
        Assert.Equal(revision, originalSource.PurchaseRevision);
        Assert.Equal((70, 65, 5), (originalSource.Layer.Quantity, originalSource.Layer.BackedQuantity,
            originalSource.Layer.UnbackedQuantity));
        Assert.DoesNotContain((await bins.Movements(a.Id, token)).Movements, m => m.PurchaseId == dense.Purchase);

        var arrival = await Save(explicitMove);
        Assert.Equal(arrival, await Save(explicitMove with { MoveAllBacking = false }));
        var full = Move(dense.Purchase, arrival.EntityId, arrival.Revision, b, c, 67) with { MoveAllBacking = true };
        var fullReceipt = await Save(full);
        Assert.Equal(fullReceipt, await Save(full));
        var final = Assert.Single((await bins.Movements(c.Id, token)).Positions, p => p.Layer.PlacementId == fullReceipt.EntityId);
        Assert.Equal((67, 65, 2), (final.Layer.Quantity, final.Layer.BackedQuantity, final.Layer.UnbackedQuantity));
        Assert.Equal(selected.Select(x => x.OfferId).Order(), final.Backing.Select(x => x.OfferId).Order());
        var eventRow = Assert.Single((await bins.Movements(c.Id, token)).Movements, m => m.Id == fullReceipt.EntityId);
        Assert.Equal(65, eventRow.Backing.Count);
        var after = Assert.Single((await receiving.Snapshot(token)).Purchases, p => p.Id == dense.Purchase);
        Assert.Equal(70, after.Balances.PhysicalOnHand);
        Assert.Equal(65, after.Balances.BackedQuantity);
        Assert.Equal(5, after.Balances.WithheldQuantity);
        Assert.All(after.Offers, offer => Assert.Equal(fullReceipt.EntityId, Assert.Single(offer.Backing).PlacementId));
    }

    private static string IsolatedDatabase()
    {
        var cs = Environment.GetEnvironmentVariable("BV_TEST_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "BV_TEST_DB with a dedicated bv_m2_test_ database is required.");
        var settings = new NpgsqlConnectionStringBuilder(cs);
        Assert.True(settings.Host is "127.0.0.1" or "::1" && settings.Username == "bv_api" &&
            settings.Database?.StartsWith("bv_m2_test_", StringComparison.Ordinal) == true,
            "Transfer tests require an isolated loopback test database and application role.");

        return cs!;
    }

    private static (long, long, long, long) Totals(BinTransferSnapshot snapshot) =>
        (snapshot.PhysicalOnHand, snapshot.BackedQuantity, snapshot.UnbackedQuantity, snapshot.FreeToBackQuantity);
}
