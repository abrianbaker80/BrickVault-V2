using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

public sealed class ReceivingTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task SplitDeliveryAndUnpublishedOfferReconcileWithoutInventingCostOrStock()
    {
        var cs = Environment.GetEnvironmentVariable("BV_TEST_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "BV_TEST_DB with a dedicated bv_m2_test_ database is required.");
        var settings = new NpgsqlConnectionStringBuilder(cs);
        Assert.True(settings.Host is "127.0.0.1" or "::1" && settings.Username == "bv_api" &&
            settings.Database?.StartsWith("bv_m2_test_", StringComparison.Ordinal) == true,
            "Receiving tests require an isolated loopback test database and application role.");

        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(cs!);
        var receiving = new Receiving(db);
        var references = new References(db);
        var prefix = Guid.NewGuid().ToString("N")[..12];
        var catalog = new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = prefix,
            Variant = null, Name = "Fictional plate", ColorId = 0, ColorState = "known", Condition = "new",
            Completeness = "complete", Source = "Fictional receiving test", License = "operator-owned",
            Mapping = "confirmed"
        };
        var item = await references.Save(catalog, Guid.NewGuid(), token);

        async Task<Guid> Bin(string code) => (await references.Save(new LocationCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code, Name = "Fixture bin"
        }, Guid.NewGuid(), token)).EntityId;

        var firstCode = "B-12-" + prefix.ToUpperInvariant();
        var secondCode = "C-03-" + prefix.ToUpperInvariant();
        var firstBin = await Bin(firstCode);
        var secondBin = await Bin(secondCode);
        var purchase = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "PO-208-" + prefix,
            Source = "Fictional invoice", Currency = "USD", OrderedQuantity = 12, PendingChargeMinor = 60
        }, Guid.NewGuid(), token);
        async Task<PurchaseRow> Read() => Assert.Single((await receiving.Snapshot(token)).Purchases,
            p => p.Id == purchase.EntityId);
        Assert.Empty((await Read()).Deliveries);

        var delivery = new DeliveryCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = purchase.Revision, PurchaseId = purchase.EntityId,
            Reference = "First delivery", Quantity = 8, CatalogId = item.EntityId, ItemCostMinor = 800
        };
        var receipts = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => receiving.Save(delivery, Guid.NewGuid(), token)));
        var first = Assert.Single(receipts.Distinct());
        Assert.Equal("command_mismatch", (await Assert.ThrowsAsync<Fault>(() =>
            receiving.Save(delivery with { ItemCostMinor = 801 }, Guid.NewGuid(), token))).Code);
        AssertStock(await Read(), 8, 8, 0);

        var putaway = new PutawayCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = first.Revision, PurchaseId = purchase.EntityId,
            DeliveryId = first.EntityId, CatalogId = item.EntityId, LocationId = firstBin,
            ConfirmedLocationCode = firstCode, Quantity = 8
        };
        Assert.Equal("bin_confirmation_mismatch", (await Assert.ThrowsAsync<Fault>(() =>
            receiving.Save(putaway with { ConfirmedLocationCode = secondCode }, Guid.NewGuid(), token))).Code);
        var intake = await Read();
        Assert.Equal(first.Revision, intake.Revision);
        AssertStock(intake, 8, 8, 0);

        var position = await receiving.Save(putaway, Guid.NewGuid(), token);
        AssertStock(await Read(), 8, 0, 8);
        var second = await receiving.Save(delivery with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = position.Revision, Reference = "Second mixed pouch",
            Quantity = 4, CatalogId = null, ItemCostMinor = null
        }, Guid.NewGuid(), token);
        AssertStock(await Read(), 12, 4, 8);
        var secondPosition = await receiving.Save(putaway with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = second.Revision, DeliveryId = second.EntityId,
            LocationId = secondBin, ConfirmedLocationCode = secondCode, Quantity = 3
        }, Guid.NewGuid(), token);
        var offer = new OfferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = secondPosition.Revision, PurchaseId = purchase.EntityId,
            CatalogId = item.EntityId, UnitPriceMinor = 150, Backing = [new(position.EntityId, 5), new(secondPosition.EntityId, 2)]
        };
        var offered = await receiving.Save(offer, Guid.NewGuid(), token);
        var stock = await Read();
        AssertStock(stock, 12, 1, 11);
        Assert.Equal(800L, stock.Balances.KnownItemCostMinor);
        Assert.Equal(860L, stock.Balances.KnownPurchaseComponentsMinor);
        Assert.Equal(60, stock.PendingChargeMinor);
        Assert.Equal(8, stock.Balances.KnownCostFreeQuantity);
        Assert.Equal(3, stock.Balances.UnknownCostFreeQuantity);
        Assert.Equal(7, stock.Balances.BackedQuantity);
        Assert.Equal(4, stock.Balances.WithheldQuantity);
        var pending = Assert.Single(stock.Deliveries, row => row.Id == second.EntityId);
        Assert.Null(pending.CatalogId);
        Assert.Null(pending.ItemCostMinor);
        Assert.Equal(1, pending.IntakeQuantity);
        Assert.Equal(secondCode, Assert.Single(pending.Placements).LocationCode);
        var ledgerOffer = Assert.Single(stock.Offers);
        Assert.Equal("USD", ledgerOffer.Currency);
        Assert.Equal(150, ledgerOffer.UnitPriceMinor);
        Assert.Equal(7, ledgerOffer.BackedQuantity);
        Assert.Equal(0, ledgerOffer.PublishedQuantity);
        Assert.True(ledgerOffer.BackingCurrent);

        var over = offer with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = offered.Revision,
            Backing = [new(position.EntityId, 4)]
        };
        Assert.Equal("quantity_unavailable", (await Assert.ThrowsAsync<Fault>(() =>
            receiving.Save(over, Guid.NewGuid(), token))).Code);
        Assert.Equal(offered.Revision, (await Read()).Revision);
        Assert.Equal(offered, await receiving.Save(offer, Guid.NewGuid(), token));

        var finalUnits = over with { Backing = [new(position.EntityId, 3)] };
        async Task<Receipt?> Compete()
        {
            try
            {
                return await receiving.Save(finalUnits with { CommandId = Guid.NewGuid() }, Guid.NewGuid(), token);
            }
            catch (Fault fault) when (fault.Code == "revision_conflict") { return null; }
        }
        var results = await Task.WhenAll(Compete(), Compete());
        var winner = Assert.Single(results.OfType<Receipt>());
        var reconciled = await Read();
        AssertStock(reconciled, 12, 1, 11);
        Assert.Equal(10, reconciled.Balances.BackedQuantity);
        Assert.Equal(1, reconciled.Balances.WithheldQuantity);
        Assert.Equal(2, reconciled.Offers.Count);
        Assert.DoesNotContain((await references.Workspace(token)).Audit, row => row.CommandId == winner.CommandId);
        Assert.Contains((await references.Workspace(token, allAudit: true)).Audit,
            row => row.CommandId == winner.CommandId && row.CorrelationId == winner.CorrelationId);
        Assert.Equal("offer", (await references.Diagnostic(winner.CorrelationId, token)).Kind);

        await references.Save(catalog with
        {
            CommandId = Guid.NewGuid(), Id = item.EntityId, ExpectedRevision = item.Revision, Condition = "used"
        }, Guid.NewGuid(), token);
        var corrected = await Read();
        Assert.Equal(reconciled.Balances, corrected.Balances);
        Assert.All(corrected.Offers, row => Assert.False(row.BackingCurrent));
        Assert.All(corrected.Deliveries.SelectMany(d => d.Placements),
            row => Assert.Equal(0, row.FreeToBackQuantity));
        Assert.Equal("backing_mismatch", (await Assert.ThrowsAsync<Fault>(() => receiving.Save(offer with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = winner.Revision,
            Backing = [new(secondPosition.EntityId, 1)]
        }, Guid.NewGuid(), token))).Code);
        Assert.Equal(winner.Revision, (await Read()).Revision);
    }

    private static void AssertStock(PurchaseRow purchase, int onHand, int unavailable, int free)
    {
        Assert.Equal(onHand, purchase.Balances.PhysicalOnHand);
        Assert.Equal(unavailable, purchase.Balances.Unavailable);
        Assert.Equal(free, purchase.Balances.Free);
        Assert.Equal(0, purchase.Balances.Reserved);
        Assert.Equal(0, purchase.Balances.PublishedQuantity);
    }
}
