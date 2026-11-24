using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace BrickVault.Api.Tests;

[CollectionDefinition("Build holds", DisableParallelization = true)]
public sealed class BuildHoldCollection;

[Collection("Build holds")]
public sealed class BuildHoldTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task NamedReleasePreservesOtherCommitmentsCostAndOpenShortage()
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
        var holds = new BuildHolds(db);
        var counts = new StockCounts(db);
        var transfers = new Transfers(db);
        var bins = new BinStock(db);
        var key = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var item = await references.Save(new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = key,
            Variant = null, Name = "Fictional fixture", ColorId = 0, ColorState = "known", Condition = "new",
            Completeness = "complete", Source = "Fictional reference", License = "operator-owned", Mapping = "confirmed"
        }, Guid.NewGuid(), token);
        Task<Receipt> Register(string code) => references.Save(new LocationCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code, Name = "Fictional bin"
        }, Guid.NewGuid(), token);
        var binCode = "HOLD-A-" + key;
        var otherCode = "HOLD-B-" + key;
        var bin = await Register(binCode);
        var otherBin = await Register(otherCode);
        var order = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "HOLD-" + key,
            Source = "Fictional supplier", Currency = "USD", OrderedQuantity = 12, PendingChargeMinor = 10
        }, Guid.NewGuid(), token);
        Task<Receipt> Deliver(int revision, string reference, int quantity, int? cost) => receiving.Save(new DeliveryCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = order.EntityId,
            Reference = reference, Quantity = quantity, CatalogId = item.EntityId, ItemCostMinor = cost
        }, Guid.NewGuid(), token);
        Task<Receipt> Place(Receipt receipt, Receipt location, string code, int quantity) => receiving.Save(new PutawayCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = receipt.Revision, PurchaseId = order.EntityId,
            DeliveryId = receipt.EntityId, CatalogId = item.EntityId, LocationId = location.EntityId,
            ConfirmedLocationCode = code, Quantity = quantity
        }, Guid.NewGuid(), token);
        var zero = await Deliver(order.Revision, "Documented zero", 8, 0);
        var origin = await Place(zero, bin, binCode, 8);
        var unknown = await Deliver(origin.Revision, "Unknown basis", 4, null);
        var sibling = await Place(unknown, otherBin, otherCode, 4);
        var offer = new OfferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = sibling.Revision, PurchaseId = order.EntityId,
            CatalogId = item.EntityId, UnitPriceMinor = 200, Backing = [new(origin.EntityId, 2)]
        };
        var promised = await receiving.Save(offer, Guid.NewGuid(), token);

        Task<Receipt> Save(BuildHoldCommand command) => holds.Save(command, Guid.NewGuid(), token);
        Task<StockHoldPositionSnapshot> Read(Guid? after = null, int limit = 25, Guid? exact = null) =>
            holds.Snapshot(bin.EntityId, origin.EntityId, after, limit, token, exact);
        async Task<string> Reject(Func<Task> write) => (await Assert.ThrowsAsync<Fault>(write)).Code;
        async Task<LossPurchaseRow> ReadPurchase() =>
            Assert.Single((await receiving.LossSnapshot(token)).Purchases, p => p.Id == order.EntityId);
        var original = await ReadPurchase();
        Assert.Equal(0, original.Balances.Reserved);
        var first = new StockHoldCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = order.EntityId, ExpectedRevision = promised.Revision,
            PositionId = origin.EntityId, LocationId = bin.EntityId, ConfirmedLocationCode = binCode,
            Quantity = 3, Purpose = "Display model", Reason = "Reserve identified pieces"
        };
        async Task CheckDatabaseRevision(BuildHoldCommand command, bool stale)
        {
            await using var direct = await db.OpenConnectionAsync(token);
            await using var tx = await direct.BeginTransactionAsync(token);
            var id = Guid.NewGuid();
            await using var insert = command switch
            {
                StockHoldCommand h => Database.Query(direct, tx, """
                    INSERT INTO brickvault.stock_build_holds(id,command_id,purchase_id,position_id,location_id,
                        location_code,expected_purchase_revision,quantity,purpose,reason)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                    """, id,h.CommandId,h.PurchaseId,h.PositionId,h.LocationId,h.ConfirmedLocationCode,
                    h.ExpectedRevision,h.Quantity,h.Purpose,h.Reason),
                StockHoldReleaseCommand r => Database.Query(direct, tx, """
                    INSERT INTO brickvault.stock_build_hold_releases(id,command_id,purchase_id,hold_id,
                        expected_purchase_revision,quantity,reason,shortage_id,expected_observation_id)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
                    """, id,r.CommandId,r.PurchaseId,r.HoldId,r.ExpectedRevision,r.Quantity,r.Reason,
                    Database.Nullable(NpgsqlDbType.Uuid,r.ShortageId),Database.Nullable(NpgsqlDbType.Uuid,r.ExpectedObservationId)),
                _ => throw new InvalidOperationException()
            };
            if (stale)
            {
                var rejected = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(token));
                Assert.Equal("hold_revision", rejected.ConstraintName);
                return;
            }
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
            await using var revision = Database.Query(direct, tx, "SELECT revision FROM brickvault.purchases WHERE id=$1",
                command.PurchaseId);
            Assert.Equal(command.ExpectedRevision + 1, await revision.ExecuteScalarAsync(token));
            // A direct event missing its command and audit cannot be committed.
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
                (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync(token))).SqlState);
        }
        await CheckDatabaseRevision(first with { ExpectedRevision = promised.Revision - 1 }, stale: true);
        await CheckDatabaseRevision(first, stale: false);
        Assert.Equal("hold_quantity_unavailable", await Reject(() => Save(first with { Quantity = 7 })));
        Assert.Equal("hold_bin_confirmation_mismatch", await Reject(() => Save(first with { ConfirmedLocationCode = otherCode })));
        var a = Assert.Single((await Task.WhenAll(Save(first), Save(first))).Distinct());
        var b = await Save(first with { CommandId = Guid.NewGuid(), ExpectedRevision = a.Revision,
            Quantity = 2, Purpose = "Workshop model" });
        Assert.Equal("hold", a.Kind);
        Assert.Equal("command_mismatch", await Reject(() => Save(first with { Quantity = 1 })));

        var page = await Read(limit: 1);
        Assert.Equal((8, 2, 5, 3, 1), (page.LedgerQuantity, page.OfferBackedQuantity, page.ReservedQuantity,
            page.FreeQuantity, page.AvailableToHoldQuantity));
        Assert.Equal(b.EntityId, Assert.Single(page.Holds).Id);
        Assert.Equal(a.EntityId, Assert.Single((await Read(page.NextAfter, 1)).Holds).Id);
        Assert.Equal(a.CorrelationId, Assert.Single((await Read(exact: a.CommandId)).Holds).CorrelationId);
        Assert.Equal("unknown_hold_command", await Reject(() => Read(exact: Guid.NewGuid())));
        Assert.Equal("invalid_hold_cursor", await Reject(() => Read(after: sibling.EntityId)));
        Assert.Equal("unknown_hold_position", await Reject(() =>
            holds.Snapshot(otherBin.EntityId, origin.EntityId, null, 25, token)));
        Assert.Equal(1, (await bins.Snapshot(bin.EntityId, token)).FreeToBackQuantity);
        Assert.Equal("receiving_client_upgrade_required", await Reject(() => receiving.CurrentSnapshot(false, token)));
        Assert.Contains("build holds", (await Assert.ThrowsAsync<Fault>(() => receiving.LegacySnapshot(token))).Message);
        var reserved = await ReadPurchase();
        Assert.Equal((12, 5, 7, 2, 5, 3, 4),
            (reserved.Balances.PhysicalOnHand, reserved.Balances.Reserved, reserved.Balances.Free,
             reserved.Balances.BackedQuantity, reserved.Balances.WithheldQuantity, reserved.Balances.KnownCostFreeQuantity,
             reserved.Balances.UnknownCostFreeQuantity));

        Assert.Equal("reserved_stock_unavailable", await Reject(() => receiving.Save(offer with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = b.Revision, Backing = [new(origin.EntityId, 2)]
        }, Guid.NewGuid(), token)));
        var move = new TransferCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = order.EntityId, ExpectedRevision = b.Revision,
            SourcePlacementId = origin.EntityId, SourceLocationId = bin.EntityId, ConfirmedSourceLocationCode = binCode,
            DestinationLocationId = otherBin.EntityId, ConfirmedDestinationLocationCode = otherCode,
            Quantity = 2, Reason = "Move uncommitted pieces", Backing = []
        };
        Assert.Equal("reserved_stock_unavailable", await Reject(() => transfers.Save(move, Guid.NewGuid(), token)));
        Assert.Empty((await bins.Movements(bin.EntityId, token)).Movements);
        var moved = await transfers.Save(move with { Quantity = 1 }, Guid.NewGuid(), token);

        var count = new StockCountCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = order.EntityId, ExpectedRevision = moved.Revision,
            PositionId = origin.EntityId, LocationId = bin.EntityId, ConfirmedLocationCode = binCode,
            CountedQuantity = 3, Reason = "Witnessed three pieces"
        };
        var observation = await counts.Save(count, Guid.NewGuid(), token);
        var shortPosition = await Read();
        Assert.Equal((7, 2, 5, 0), (shortPosition.LedgerQuantity, shortPosition.OfferBackedQuantity,
            shortPosition.ReservedQuantity, shortPosition.AvailableToHoldQuantity));
        Assert.Equal(observation.EntityId, shortPosition.ShortageId);
        Assert.Equal(new[] { 2, 3 }, shortPosition.Holds.Select(h => h.RemainingQuantity));
        Assert.Equal("stock_shortage_open", await Reject(() => Save(first with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = observation.Revision, Quantity = 1
        })));

        var recount = await counts.Save(count with { CommandId = Guid.NewGuid(), ExpectedRevision = observation.Revision },
            Guid.NewGuid(), token);
        var release = new StockHoldReleaseCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = order.EntityId, ExpectedRevision = recount.Revision,
            HoldId = a.EntityId, Quantity = 1, Reason = "Owner selected one display model piece",
            ShortageId = observation.EntityId, ExpectedObservationId = recount.EntityId
        };
        await CheckDatabaseRevision(release with { ExpectedRevision = recount.Revision - 1 }, stale: true);
        await CheckDatabaseRevision(release, stale: false);
        Assert.Equal("hold_shortage_frontier_conflict", await Reject(() =>
            Save(release with { ExpectedObservationId = observation.EntityId })));
        Assert.Equal("hold_shortage_frontier_conflict", await Reject(() =>
            Save(release with { ShortageId = null, ExpectedObservationId = null })));
        Assert.Equal("hold_quantity_unavailable", await Reject(() => Save(release with { Quantity = 4 })));

        async Task<(StockHoldReleaseCommand Request, Receipt? Receipt)> Race(StockHoldReleaseCommand request)
        {
            try { return (request, await Save(request)); }
            catch (Fault e) when (e.Code == "revision_conflict") { return (request, null); }
        }
        var rival = release with { CommandId = Guid.NewGuid(), Reason = "Rival decision at the same revision" };
        var candidates = await Task.WhenAll(Race(release), Race(rival));
        var winning = Assert.Single(candidates, c => c.Receipt is not null);
        var released = Assert.IsType<Receipt>(winning.Receipt);
        Assert.Equal("hold-release", released.Kind);
        Assert.Equal(released, await Save(winning.Request));
        Assert.Equal("revision_conflict", await Reject(() => Save(Assert.Single(candidates, c => c.Receipt is null).Request)));
        var evidence = await holds.Releases(bin.EntityId, origin.EntityId, a.EntityId, null, 25, token, released.CommandId);
        var entry = Assert.Single(evidence.Releases);
        Assert.Equal((released.EntityId, released.CorrelationId, winning.Request.Reason, 1, observation.EntityId, recount.EntityId),
            (entry.Id, entry.CorrelationId, entry.Reason, entry.Quantity, entry.ShortageId, entry.ExpectedObservationId));
        Assert.Empty((await holds.Releases(bin.EntityId, origin.EntityId, b.EntityId, null, 25, token)).Releases);

        var final = await Read();
        Assert.Equal((7, 2, 4, 3, 0), (final.LedgerQuantity, final.OfferBackedQuantity, final.ReservedQuantity,
            final.FreeQuantity, final.AvailableToHoldQuantity));
        Assert.Equal(2, Assert.Single(final.Holds, h => h.Id == a.EntityId).RemainingQuantity);
        Assert.Equal(2, Assert.Single(final.Holds, h => h.Id == b.EntityId).RemainingQuantity);
        var shortage = Assert.Single((await counts.Snapshot(bin.EntityId, token, origin.EntityId)).Shortages);
        Assert.Equal(("open", observation.EntityId, recount.EntityId), (shortage.State, shortage.Id, shortage.LatestObservationId));
        Assert.False(Assert.Single((await counts.Snapshot(bin.EntityId, token, observationId: observation.EntityId)).Observations).PostedAdjustment);

        var purchase = await ReadPurchase();
        Assert.Equal((12, 4, 8, 2, 6, 4, 4), (purchase.Balances.PhysicalOnHand, purchase.Balances.Reserved,
            purchase.Balances.Free, purchase.Balances.BackedQuantity, purchase.Balances.WithheldQuantity,
            purchase.Balances.KnownCostFreeQuantity, purchase.Balances.UnknownCostFreeQuantity));
        Assert.Equal(0, Assert.Single(purchase.Deliveries, d => d.Id == zero.EntityId).ItemCostMinor);
        Assert.Null(Assert.Single(purchase.Deliveries, d => d.Id == unknown.EntityId).ItemCostMinor);
        Assert.Equal("USD", purchase.Currency);
        Assert.Equal(10, purchase.Balances.KnownPurchaseComponentsMinor);
        var unchanged = Assert.Single(purchase.Offers);
        Assert.Equal((promised.EntityId, 200, 2, 0), (unchanged.Id, unchanged.UnitPriceMinor, unchanged.BackedQuantity, unchanged.PublishedQuantity));
        Assert.Equal(promised, await receiving.Save(offer, Guid.NewGuid(), token));
        Assert.Equal("hold-release", (await references.Diagnostic(released.CorrelationId, token)).Kind);
        Assert.DoesNotContain((await references.Workspace(token, allAudit: true)).Audit,
            row => row.CommandId == a.CommandId || row.CommandId == released.CommandId);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var grants = Database.Query(connection, null, """
            SELECT NOT has_table_privilege('brickvault.stock_build_holds','UPDATE,DELETE')
               AND NOT has_table_privilege('brickvault.stock_build_hold_releases','UPDATE,DELETE')
               AND NOT has_table_privilege('bv_worker','brickvault.stock_build_holds','INSERT,UPDATE,DELETE')
               AND NOT has_table_privilege('bv_worker','brickvault.stock_build_hold_releases','INSERT,UPDATE,DELETE')
            """);
        Assert.Equal(true, await grants.ExecuteScalarAsync(token));

        var closeA = await Save(release with { CommandId = Guid.NewGuid(), ExpectedRevision = released.Revision, Quantity = 2 });
        var closeB = await Save(release with { CommandId = Guid.NewGuid(), ExpectedRevision = closeA.Revision, HoldId = b.EntityId, Quantity = 2 });
        Assert.Equal(0, (await ReadPurchase()).Balances.Reserved);
        Assert.Equal(0, (await Read()).ReservedQuantity);

        var independent = await Save(first with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = closeB.Revision, PositionId = sibling.EntityId,
            LocationId = otherBin.EntityId, ConfirmedLocationCode = otherCode, Quantity = 1
        });
        var unknownReserved = await ReadPurchase();
        Assert.Equal((1, 8, 3), (unknownReserved.Balances.Reserved, unknownReserved.Balances.KnownCostFreeQuantity,
            unknownReserved.Balances.UnknownCostFreeQuantity));
        var ordinaryRelease = await Save(new StockHoldReleaseCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = order.EntityId, ExpectedRevision = independent.Revision,
            HoldId = independent.EntityId, Quantity = 1, Reason = "Internal build completed"
        });
        var independentEvidence = await holds.Releases(otherBin.EntityId, sibling.EntityId, independent.EntityId,
            null, 25, token, ordinaryRelease.CommandId);
        var ordinary = Assert.Single(independentEvidence.Releases);
        Assert.Null(ordinary.ShortageId);
        Assert.Null(ordinary.ExpectedObservationId);
        Assert.Equal(original.Balances, (await ReadPurchase()).Balances);
    }
}
