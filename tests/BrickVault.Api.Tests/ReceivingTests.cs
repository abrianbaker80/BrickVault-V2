using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

public sealed class ReceivingTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task SplitDeliveryAndUnpublishedOfferReconcileWithoutInventingCostOrStock()
    {
        var cs = IsolatedDatabase();
        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(cs);
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
            LocationId = secondBin, ConfirmedLocationCode = secondCode, ConfirmedCatalogRevision = item.Revision, Quantity = 3
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

        var olderIntakePurchase = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "PO-older-intake-" + prefix,
            Source = "Fictional invoice", Currency = "USD", OrderedQuantity = 8, PendingChargeMinor = null
        }, Guid.NewGuid(), token);
        var olderDelivery = await receiving.Save(delivery with
        {
            CommandId = Guid.NewGuid(), PurchaseId = olderIntakePurchase.EntityId,
            ExpectedRevision = olderIntakePurchase.Revision
        }, Guid.NewGuid(), token);
        var firstPlacementCommand = putaway with
        {
            CommandId = Guid.NewGuid(), PurchaseId = olderIntakePurchase.EntityId, DeliveryId = olderDelivery.EntityId,
            ExpectedRevision = olderDelivery.Revision, Quantity = 3
        };
        var firstPlacement = await receiving.Save(firstPlacementCommand, Guid.NewGuid(), token);

        var correction = await references.Save(catalog with
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

        async Task<PurchaseRow> ReadOlderIntake() => Assert.Single((await receiving.Snapshot(token)).Purchases,
            p => p.Id == olderIntakePurchase.EntityId);
        var remaining = firstPlacementCommand with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = firstPlacement.Revision, Quantity = 2
        };
        Assert.Equal("catalog_reconfirmation_required", (await Assert.ThrowsAsync<Fault>(() =>
            receiving.Save(remaining, Guid.NewGuid(), token))).Code);
        Assert.Equal("catalog_reconfirmation_required", (await Assert.ThrowsAsync<Fault>(() =>
            receiving.Save(remaining with { ConfirmedCatalogRevision = item.Revision }, Guid.NewGuid(), token))).Code);
        var wrongItem = await references.Save(catalog with
        {
            CommandId = Guid.NewGuid(), ItemNumber = prefix + "-other"
        }, Guid.NewGuid(), token);
        Assert.Equal("catalog_ineligible", (await Assert.ThrowsAsync<Fault>(() =>
            receiving.Save(remaining with { CatalogId = wrongItem.EntityId, ConfirmedCatalogRevision = wrongItem.Revision },
                Guid.NewGuid(), token))).Code);
        var rejected = await ReadOlderIntake();
        Assert.Equal(firstPlacement.Revision, rejected.Revision);
        AssertStock(rejected, 8, 5, 3);

        var confirmed = remaining with { ConfirmedCatalogRevision = correction.Revision };
        var reconfirmed = await receiving.Save(confirmed, Guid.NewGuid(), token);
        Assert.Equal(reconfirmed, await receiving.Save(confirmed, Guid.NewGuid(), token));
        Assert.Equal(firstPlacement, await receiving.Save(firstPlacementCommand, Guid.NewGuid(), token));
        var reconfirmedPurchase = await ReadOlderIntake();
        AssertStock(reconfirmedPurchase, 8, 3, 5);
        var unchangedDelivery = Assert.Single(reconfirmedPurchase.Deliveries);
        Assert.Equal(olderDelivery.EntityId, unchangedDelivery.Id);
        Assert.Equal(item.EntityId, unchangedDelivery.CatalogId);
        Assert.Equal(item.Revision, unchangedDelivery.CatalogRevision);
        Assert.Equal(800, unchangedDelivery.ItemCostMinor);
        Assert.Equal(item.Revision, Assert.Single(unchangedDelivery.Placements, row => row.Id == firstPlacement.EntityId).CatalogRevision);
        Assert.Equal(correction.Revision, Assert.Single(unchangedDelivery.Placements, row => row.Id == reconfirmed.EntityId).CatalogRevision);
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task MixedPutawayRejectsARevisionChangedWhileItsPostgresLockWaits()
    {
        var cs = IsolatedDatabase();
        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(cs);
        var receiving = new Receiving(db);
        var references = new References(db);
        var prefix = Guid.NewGuid().ToString("N")[..12];
        var code = "RACE-" + prefix.ToUpperInvariant();
        var bin = await references.Save(new LocationCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code, Name = "Race fixture bin"
        }, Guid.NewGuid(), token);
        var item = await references.Save(new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = prefix, Variant = null,
            Name = "Fictional mixed intake", ColorId = 0, ColorState = "known", Condition = "new",
            Completeness = "complete", Source = "Fictional race fixture", License = "operator-owned",
            Mapping = "confirmed"
        }, Guid.NewGuid(), token);
        var order = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "RACE-" + prefix, Source = "Fictional source",
            Currency = "USD", OrderedQuantity = 2, PendingChargeMinor = null
        }, Guid.NewGuid(), token);
        var delivery = await receiving.Save(new DeliveryCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = order.Revision, PurchaseId = order.EntityId,
            Reference = "Mixed pouch", Quantity = 2, CatalogId = null, ItemCostMinor = null
        }, Guid.NewGuid(), token);
        var request = new PutawayCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = delivery.Revision, PurchaseId = order.EntityId,
            DeliveryId = delivery.EntityId, CatalogId = item.EntityId, LocationId = bin.EntityId,
            ConfirmedLocationCode = code, Quantity = 1
        };
        Assert.Equal("catalog_reconfirmation_required", (await Assert.ThrowsAsync<Fault>(() =>
            receiving.Save(request, Guid.NewGuid(), token))).Code);
        Assert.Equal("catalog_ineligible", (await Assert.ThrowsAsync<Fault>(() =>
            receiving.Save(request with { CatalogId = Guid.NewGuid(), ConfirmedCatalogRevision = item.Revision },
                Guid.NewGuid(), token))).Code);

        // Hold the real catalog write lock after editing its condition but before commit.
        await using var editor = await db.OpenConnectionAsync(token);
        await using var edit = await editor.BeginTransactionAsync(token);
        await using (var change = Database.Query(editor, edit, """
            UPDATE brickvault.catalog SET condition='used',revision=revision+1
            WHERE id=$1 AND revision=$2 RETURNING revision
            """, item.EntityId, item.Revision))
            Assert.Equal(item.Revision + 1, await change.ExecuteScalarAsync(token));

        var applicationName = "mixed-putaway-" + prefix;
        var watcherSettings = new NpgsqlConnectionStringBuilder(cs) { ApplicationName = applicationName };
        await using var concurrent = NpgsqlDataSource.Create(watcherSettings.ConnectionString);
        var pending = new Receiving(concurrent).Save(request with { ConfirmedCatalogRevision = item.Revision },
            Guid.NewGuid(), token);
        await using var observer = await db.OpenConnectionAsync(token);
        await using var check = Database.Query(observer, null, """
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE application_name=$1 AND $2=ANY(pg_blocking_pids(pid)))
            """, applicationName, editor.ProcessID);
        var until = DateTime.UtcNow.AddSeconds(5);
        var blocked = false;
        while (!pending.IsCompleted && DateTime.UtcNow < until)
        {
            blocked = (bool)(await check.ExecuteScalarAsync(token))!;
            if (blocked) break;
            await Task.Delay(25, token);
        }

        await edit.CommitAsync(token);
        var denied = await Assert.ThrowsAsync<Fault>(() => pending);
        Assert.True(blocked, "The putaway request must have raced the uncommitted catalog edit.");
        Assert.Equal((409, "catalog_reconfirmation_required"), (denied.Status, denied.Code));
        async Task<PurchaseRow> Read() => Assert.Single((await receiving.Snapshot(token)).Purchases,
            p => p.Id == order.EntityId);
        var held = await Read();
        AssertStock(held, 2, 2, 0);
        Assert.Equal(delivery.Revision, held.Revision);
        Assert.Empty(Assert.Single(held.Deliveries).Placements);
        Assert.DoesNotContain((await references.Workspace(token, allAudit: true)).Audit,
            row => row.CommandId == request.CommandId);

        var confirmed = request with { ConfirmedCatalogRevision = item.Revision + 1 };
        var receipt = await receiving.Save(confirmed, Guid.NewGuid(), token);
        Assert.Equal(receipt, await receiving.Save(confirmed, Guid.NewGuid(), token));
        var placed = await Read();
        AssertStock(placed, 2, 1, 1);
        Assert.Equal(item.Revision + 1, Assert.Single(Assert.Single(placed.Deliveries).Placements).CatalogRevision);
    }

    private static string IsolatedDatabase()
    {
        var cs = Environment.GetEnvironmentVariable("BV_TEST_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "BV_TEST_DB with a dedicated bv_m2_test_ database is required.");
        var settings = new NpgsqlConnectionStringBuilder(cs);
        Assert.True(settings.Host is "127.0.0.1" or "::1" && settings.Username == "bv_api" &&
            settings.Database?.StartsWith("bv_m2_test_", StringComparison.Ordinal) == true,
            "Receiving tests require an isolated loopback test database and application role.");

        return cs!;
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
