using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

public sealed class StockCountTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task ShortObservationFencesOnlyItsPositionAndPreservesLedgerAndOfferPromises()
    {
        var cs = Environment.GetEnvironmentVariable("BV_TEST_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "BV_TEST_DB with a dedicated bv_m2_test_ database is required.");
        var settings = new NpgsqlConnectionStringBuilder(cs);
        Assert.True(settings.Host is "127.0.0.1" or "::1" && settings.Username == "bv_api" &&
            settings.Database?.StartsWith("bv_m2_test_", StringComparison.Ordinal) == true,
            "Count tests require an isolated loopback test database and application role.");

        var token = TestContext.Current.CancellationToken;
        await using var db = NpgsqlDataSource.Create(cs!);
        var references = new References(db);
        var receiving = new Receiving(db);
        var counts = new StockCounts(db);
        var bins = new BinStock(db);
        var transfers = new Transfers(db);
        var inventory = new StockRegister(db);
        var key = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var item = await references.Save(new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = key,
            Variant = null, Name = "Fictional counting fixture", ColorId = 0, ColorState = "known",
            Condition = "new", Completeness = "complete", Source = "Fictional reference", License = "operator-owned",
            Mapping = "confirmed"
        }, Guid.NewGuid(), token);

        async Task<LocationRow> Register(string code)
        {
            var location = await references.Save(new LocationCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code + key, Name = "Fictional shelf"
            }, Guid.NewGuid(), token);
            return new LocationRow(location.EntityId, location.Revision, code + key, "Fictional shelf");
        }
        var a = await Register("CNT-A-");
        var b = await Register("CNT-B-");
        var c = await Register("CNT-C-");

        var order = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "CNT-" + key,
            Source = "Fictional supplier", Currency = "USD", OrderedQuantity = 10, PendingChargeMinor = null
        }, Guid.NewGuid(), token);
        Task<Receipt> Deliver(int revision, string label, int quantity, int? cost) =>
            receiving.Save(new DeliveryCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = order.EntityId,
                Reference = label, Quantity = quantity, CatalogId = item.EntityId, ItemCostMinor = cost
            }, Guid.NewGuid(), token);
        Task<Receipt> Place(Receipt delivery, LocationRow location, int quantity, int? expectedRevision = null) =>
            receiving.Save(new PutawayCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = expectedRevision ?? delivery.Revision, PurchaseId = order.EntityId,
                DeliveryId = delivery.EntityId, CatalogId = item.EntityId, LocationId = location.Id,
                ConfirmedLocationCode = location.Code, Quantity = quantity
            }, Guid.NewGuid(), token);
        var zero = await Deliver(order.Revision, "Documented zero", 6, 0);
        var origin = await Place(zero, a, 6);
        var unknown = await Deliver(origin.Revision, "Unknown basis", 4, null);
        var other = await Place(unknown, b, 3);
        var sameBinOther = await Place(unknown, a, 1, other.Revision);
        var terms = new OfferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = sameBinOther.Revision, PurchaseId = order.EntityId,
            CatalogId = item.EntityId, UnitPriceMinor = 200, Backing = [new(origin.EntityId, 3), new(other.EntityId, 1)]
        };
        var offer = await receiving.Save(terms, Guid.NewGuid(), token);
        Task<Receipt> Count(StockCountCommand command) => counts.Save(command, Guid.NewGuid(), token);
        async Task<PurchaseRow> Purchase() =>
            Assert.Single((await receiving.Snapshot(token)).Purchases,p => p.Id == order.EntityId);
        var before = await Purchase();
        Assert.Empty((await counts.Snapshot(c.Id, token)).Positions);
        Assert.Equal("unknown_location", (await Assert.ThrowsAsync<Fault>(() => counts.Snapshot(Guid.NewGuid(), token))).Code);

        var request = new StockCountCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = order.EntityId, ExpectedRevision = offer.Revision,
            PositionId = origin.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code,
            CountedQuantity = 0, Reason = "Witnessed empty position"
        };
        Assert.Equal("count_bin_confirmation_mismatch", (await Assert.ThrowsAsync<Fault>(() =>
            Count(request with { ConfirmedLocationCode = b.Code }))).Code);
        Assert.Equal("count_position_mismatch", (await Assert.ThrowsAsync<Fault>(() =>
            Count(request with { PositionId = other.EntityId }))).Code);
        Assert.Equal("invalid_count", (await Assert.ThrowsAsync<Fault>(() =>
            Count(request with { CountedQuantity = -1 }))).Code);
        Assert.Empty((await counts.Snapshot(a.Id, token)).Observations);
        Assert.Equal(offer.Revision, (await Purchase()).Revision);

        var repeated = await Task.WhenAll(Count(request), Count(request));
        var receipt = Assert.Single(repeated.Distinct());
        Assert.Equal("count", receipt.Kind);
        Assert.Equal("command_mismatch", (await Assert.ThrowsAsync<Fault>(() =>
            Count(request with { CountedQuantity = 1 }))).Code);
        Assert.Equal("revision_conflict", (await Assert.ThrowsAsync<Fault>(() =>
            Count(request with { CommandId = Guid.NewGuid() }))).Code);
        var snapshot = await counts.Snapshot(a.Id, token, origin.EntityId);
        Assert.Equal("bin-counts", snapshot.Scope);
        Assert.True(snapshot.Complete);
        var position = Assert.Single(snapshot.Positions);
        Assert.True(position.ShortageOpen);
        Assert.Equal(receipt.Revision, position.PurchaseRevision);
        var observation = Assert.Single(snapshot.Observations);
        Assert.Equal(receipt.CorrelationId, observation.CorrelationId);
        Assert.Equal((6, 3, 0, 6, 0, false), (observation.LedgerQuantity, observation.OfferBackedQuantity,
            observation.CountedQuantity, observation.MissingQuantity, observation.UnidentifiedSurplusQuantity,
            observation.PostedAdjustment));
        var shortage = Assert.Single(snapshot.Shortages);
        Assert.Equal(observation.Id, shortage.Id);
        Assert.Equal("open", shortage.State);
        Assert.Equal(6, shortage.OpeningMissingQuantity);
        Assert.Empty((await counts.Snapshot(b.Id, token)).Shortages);

        Assert.Equal("stock_shortage_open", (await Assert.ThrowsAsync<Fault>(() => receiving.Save(terms with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = receipt.Revision, Backing = [new(origin.EntityId, 1)]
        }, Guid.NewGuid(), token))).Code);
        var move = new TransferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = receipt.Revision, PurchaseId = order.EntityId,
            SourcePlacementId = origin.EntityId, SourceLocationId = a.Id, ConfirmedSourceLocationCode = a.Code,
            DestinationLocationId = c.Id, ConfirmedDestinationLocationCode = c.Code, Quantity = 1,
            Reason = "Fictional stock handling", Backing = []
        };
        Assert.Equal("stock_shortage_open", (await Assert.ThrowsAsync<Fault>(() =>
            transfers.Save(move, Guid.NewGuid(), token))).Code);
        Assert.Empty((await bins.Movements(a.Id, token)).Movements);
        Assert.Equal(receipt.Revision, (await Purchase()).Revision);

        async Task<Receipt?> Recount()
        {
            try { return await Count(request with
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = receipt.Revision, CountedQuantity = 7,
                Reason = "Witnessed an unidentified extra piece"
            }); }
            catch (Fault e) when (e.Code == "revision_conflict") { return null; }
        }
        var recount = Assert.Single((await Task.WhenAll(Recount(), Recount())).OfType<Receipt>());
        var later = await counts.Snapshot(a.Id, token, origin.EntityId);
        Assert.Equal(2, later.Observations.Count);
        var laterCount = Assert.Single(later.Observations, row => row.Id == recount.EntityId);
        Assert.Equal(1, laterCount.UnidentifiedSurplusQuantity);
        Assert.False(laterCount.PostedAdjustment);
        var stillOpen = Assert.Single(later.Shortages);
        Assert.Equal(shortage.Id, stillOpen.Id);
        Assert.Equal("open", stillOpen.State);
        Assert.Equal(6, stillOpen.OpeningMissingQuantity);
        Assert.Equal(0, stillOpen.LatestObservedMissingQuantity);
        Assert.Equal(recount.EntityId, stillOpen.LatestObservationId);

        var separate = await Count(request with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = recount.Revision,
            PositionId = sameBinOther.EntityId, CountedQuantity = 1, Reason = "Independent position in this bin"
        });
        var binPage = await counts.Snapshot(a.Id, token, limit: 1);
        Assert.Equal(2, binPage.Positions.Count);
        Assert.Null(binPage.PositionId);
        Assert.Equal(separate.EntityId, Assert.Single(binPage.Observations).Id);
        Assert.Equal(separate.EntityId, binPage.NextAfter);
        Assert.Equal(stillOpen, Assert.Single(binPage.Shortages));
        var firstPage = await counts.Snapshot(a.Id, token, origin.EntityId, limit: 1);
        Assert.Equal(origin.EntityId, firstPage.PositionId);
        Assert.Equal(origin.EntityId, Assert.Single(firstPage.Positions).PositionId);
        Assert.Equal(recount.EntityId, Assert.Single(firstPage.Observations).Id);
        Assert.Equal(recount.EntityId, firstPage.NextAfter);
        Assert.Equal(stillOpen, Assert.Single(firstPage.Shortages));
        var olderPage = await counts.Snapshot(a.Id, token, origin.EntityId, firstPage.NextAfter, 1);
        Assert.Equal(receipt.EntityId, Assert.Single(olderPage.Observations).Id);
        Assert.Null(olderPage.NextAfter);
        Assert.Equal(stillOpen, Assert.Single(olderPage.Shortages));
        var exact = await counts.Snapshot(a.Id, token, observationId: receipt.EntityId);
        Assert.Equal(origin.EntityId, exact.PositionId);
        Assert.Equal(receipt.EntityId, Assert.Single(exact.Observations).Id);
        Assert.Equal(stillOpen, Assert.Single(exact.Shortages));
        Assert.Null(exact.NextAfter);
        var replayLookup = await counts.Snapshot(a.Id, token, origin.EntityId, commandId: receipt.CommandId);
        Assert.Equal(receipt.CorrelationId, Assert.Single(replayLookup.Observations).CorrelationId);
        Assert.Equal("invalid_count_cursor", (await Assert.ThrowsAsync<Fault>(() =>
            counts.Snapshot(a.Id, token, origin.EntityId, separate.EntityId, 1))).Code);
        Assert.Equal("unknown_count_position", (await Assert.ThrowsAsync<Fault>(() =>
            counts.Snapshot(b.Id, token, origin.EntityId))).Code);
        Assert.Equal("unknown_count_observation", (await Assert.ThrowsAsync<Fault>(() =>
            counts.Snapshot(a.Id, token, sameBinOther.EntityId, observationId: receipt.EntityId))).Code);

        var unaffected = await transfers.Save(move with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = separate.Revision, SourcePlacementId = other.EntityId,
            SourceLocationId = b.Id, ConfirmedSourceLocationCode = b.Code
        }, Guid.NewGuid(), token);
        var after = await Purchase();
        Assert.Equal(unaffected.Revision, after.Revision);
        Assert.Equal(before.Balances, after.Balances);
        Assert.Equal(0, Assert.Single(after.Deliveries, row => row.Id == zero.EntityId).ItemCostMinor);
        Assert.Null(Assert.Single(after.Deliveries, row => row.Id == unknown.EntityId).ItemCostMinor);
        Assert.Equal(4, Assert.Single(after.Offers).BackedQuantity);
        Assert.Equal(3, Assert.Single(Assert.Single(after.Offers).Backing, line => line.PlacementId == origin.EntityId).Quantity);
        Assert.Equal(0, Assert.Single(Assert.Single(after.Deliveries, row => row.Id == zero.EntityId).Placements).FreeToBackQuantity);
        Assert.Equal(1, (await bins.Snapshot(a.Id, token)).FreeToBackQuantity);
        var register = await inventory.Search(null, null, null, origin.EntityId, null, 25, token);
        Assert.Equal(0, Assert.Single(register.Rows).Layer.FreeToBackQuantity);
        Assert.Equal(1, (await bins.Snapshot(c.Id, token)).FreeToBackQuantity);
        Assert.Equal(offer, await receiving.Save(terms, Guid.NewGuid(), token));
        Assert.Equal(receipt, await Count(request));
        Assert.DoesNotContain((await references.Workspace(token, allAudit: true)).Audit, a => a.CommandId == receipt.CommandId);
        Assert.Equal("count", (await references.Diagnostic(receipt.CorrelationId, token)).Kind);

        await using var connection = await db.OpenConnectionAsync(token);
        await using var permissions = Database.Query(connection, null, """
            SELECT NOT has_column_privilege('brickvault.stock_count_observations','ledger_quantity','INSERT')
               AND NOT has_table_privilege('brickvault.stock_shortage_cases','INSERT')
               AND NOT has_table_privilege('brickvault.stock_count_observations','UPDATE,DELETE')
            """);
        Assert.Equal(true, await permissions.ExecuteScalarAsync(token));
    }
}
