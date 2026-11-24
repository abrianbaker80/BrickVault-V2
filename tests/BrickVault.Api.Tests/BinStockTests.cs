using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

public sealed class BinStockTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task SelectedBinRetainsSeparateLayersAcrossPurchasesAndRecordedIdentity()
    {
        var cs = Environment.GetEnvironmentVariable("BV_TEST_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "BV_TEST_DB with a dedicated bv_m2_test_ database is required.");
        var settings = new NpgsqlConnectionStringBuilder(cs);
        Assert.True(settings.Host is "127.0.0.1" or "::1" && settings.Username == "bv_api" &&
            settings.Database?.StartsWith("bv_m2_test_", StringComparison.Ordinal) == true,
            "Bin stock tests require an isolated loopback test database and application role.");

        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(cs!);
        var bins = new BinStock(db);
        var receiving = new Receiving(db);
        var references = new References(db);
        var prefix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var catalog = new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = prefix,
            Variant = "a", Name = "Fictional plate", ColorId = 0, ColorState = "known", Condition = "new",
            Completeness = "complete", Source = "Fictional bin test", License = "operator-owned", Mapping = "confirmed"
        };
        var item = await references.Save(catalog, Guid.NewGuid(), token);
        async Task<LocationRow> RegisterBin(string code)
        {
            var receipt = await references.Save(new LocationCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code + prefix, Name = "Fixture bin"
            }, Guid.NewGuid(), token);
            return new LocationRow(receipt.EntityId, receipt.Revision, code + prefix, "Fixture bin");
        }
        var selected = await RegisterBin("B1-");
        var second = await RegisterBin("C1-");
        var empty = await RegisterBin("D1-");

        var purchaseA = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "BIN-A-" + prefix,
            Source = "Fictional supplier", Currency = "USD", OrderedQuantity = 10, PendingChargeMinor = null
        }, Guid.NewGuid(), token);
        var purchaseB = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "BIN-B-" + prefix,
            Source = "Fictional supplier", Currency = "JPY", OrderedQuantity = 5, PendingChargeMinor = null
        }, Guid.NewGuid(), token);

        Task<Receipt> Deliver(Receipt purchase, int revision, string code, int quantity, int? cost) =>
            receiving.Save(new DeliveryCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
                Reference = code, Quantity = quantity, CatalogId = item.EntityId, ItemCostMinor = cost
            }, Guid.NewGuid(), token);

        Task<Receipt> PutAway(Receipt purchase, Receipt delivery, int revision, LocationRow location, int quantity) =>
            receiving.Save(new PutawayCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
                DeliveryId = delivery.EntityId, CatalogId = item.EntityId, LocationId = location.Id,
                ConfirmedLocationCode = location.Code, Quantity = quantity
            }, Guid.NewGuid(), token);

        var zero = await Deliver(purchaseA, purchaseA.Revision, "Documented zero", 6, 0);
        var selectedZero = await PutAway(purchaseA, zero, zero.Revision, selected, 3);
        var otherZero = await PutAway(purchaseA, zero, selectedZero.Revision, second, 2);
        var paid = await Deliver(purchaseA, otherZero.Revision, "Documented amount", 4, 851);
        var selectedPaid = await PutAway(purchaseA, paid, paid.Revision, selected, 1);
        var otherPaid = await PutAway(purchaseA, paid, selectedPaid.Revision, second, 2);
        var unknown = await Deliver(purchaseB, purchaseB.Revision, "Unknown amount", 5, null);
        var selectedUnknown = await PutAway(purchaseB, unknown, unknown.Revision, selected, 4);

        var offer = new OfferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = otherPaid.Revision, PurchaseId = purchaseA.EntityId,
            CatalogId = item.EntityId, UnitPriceMinor = 150, Backing = [new(selectedZero.EntityId, 2)]
        };
        var offerReceipt = await receiving.Save(offer, Guid.NewGuid(), token);
        await receiving.Save(offer with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = selectedUnknown.Revision, PurchaseId = purchaseB.EntityId,
            Backing = [new(selectedUnknown.EntityId, 1)]
        }, Guid.NewGuid(), token);

        var before = await bins.Snapshot(selected.Id, token);
        Assert.True(before.Complete);
        Assert.Equal("bin-stock", before.Scope);
        Assert.Equal(DateTimeKind.Utc, before.AsOf.Kind);
        Assert.Equal(selected, before.Location);
        Assert.Equal((8L, 3L, 5L, 5L), (before.PhysicalOnHand, before.BackedQuantity,
            before.UnbackedQuantity, before.FreeToBackQuantity));
        Assert.Equal(3, before.Layers.Count);
        var zeroLayer = Assert.Single(before.Layers, x => x.PlacementId == selectedZero.EntityId);
        Assert.Equal((3, 2, 1, 1), (zeroLayer.Quantity, zeroLayer.BackedQuantity, zeroLayer.UnbackedQuantity,
            zeroLayer.FreeToBackQuantity));
        Assert.Equal((zero.EntityId, zero.CommandId, zero.CorrelationId),
            (zeroLayer.DeliveryId, zeroLayer.ReceiptCommandId, zeroLayer.ReceiptCorrelationId));
        Assert.Equal(0, zeroLayer.ReceiptItemCostMinor);
        Assert.Equal("USD", zeroLayer.Currency);
        var paidLayer = Assert.Single(before.Layers, x => x.PlacementId == selectedPaid.EntityId);
        Assert.Equal(851, paidLayer.ReceiptItemCostMinor);
        Assert.Equal(4, paidLayer.ReceiptQuantity);
        Assert.Equal(1, paidLayer.Quantity);
        var unknownLayer = Assert.Single(before.Layers, x => x.PlacementId == selectedUnknown.EntityId);
        Assert.Equal(purchaseB.EntityId, unknownLayer.PurchaseId);
        Assert.Null(unknownLayer.ReceiptItemCostMinor);
        Assert.Equal("JPY", unknownLayer.Currency);

        var outside = await bins.Snapshot(second.Id, token);
        Assert.Equal(4L, outside.PhysicalOnHand);
        Assert.Equal(new[] { otherZero.EntityId, otherPaid.EntityId }.Order(), outside.Layers.Select(x => x.PlacementId).Order());
        Assert.Equal(851, Assert.Single(outside.Layers, x => x.PlacementId == otherPaid.EntityId).ReceiptItemCostMinor);
        var noStock = await bins.Snapshot(empty.Id, token);
        Assert.True(noStock.Complete);
        Assert.Equal(empty, noStock.Location);
        Assert.Empty(noStock.Layers);
        Assert.Equal((0L, 0L, 0L, 0L), (noStock.PhysicalOnHand, noStock.BackedQuantity,
            noStock.UnbackedQuantity, noStock.FreeToBackQuantity));
        var missing = await Assert.ThrowsAsync<Fault>(() => bins.Snapshot(Guid.NewGuid(), token));
        Assert.Equal((404, "unknown_location"), (missing.Status, missing.Code));

        var edit = await references.Save(catalog with
        {
            CommandId = Guid.NewGuid(), Id = item.EntityId, ExpectedRevision = item.Revision, Name = "Corrected plate"
        }, Guid.NewGuid(), token);
        var after = await bins.Snapshot(selected.Id, token);
        Assert.Equal((8L, 3L, 5L, 0L), (after.PhysicalOnHand, after.BackedQuantity,
            after.UnbackedQuantity, after.FreeToBackQuantity));
        Assert.All(after.Layers, layer =>
        {
            Assert.Equal(item.Revision, layer.CatalogRevision);
            Assert.Equal(edit.Revision, layer.CurrentCatalog.Revision);
            Assert.Equal("Corrected plate", layer.CurrentCatalog.Name);
            Assert.True(layer.CurrentCatalog.IntegrationEligible);
        });
        Assert.Equal(offerReceipt, await receiving.Save(offer, Guid.NewGuid(), token));
        Assert.Equal(offerReceipt.CorrelationId, (await references.Diagnostic(offerReceipt.CorrelationId, token)).CorrelationId);
    }
}
