using System.Text.Json;
using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

[Collection("Build holds")]
public sealed class StockQuarantineTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task QuarantineProtectsPhysicalStockAndCommitmentsUntilAnExactInspectedRelease()
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
        var quarantines = new StockQuarantines(db);
        var holds = new BuildHolds(db);
        var bins = new BinStock(db);
        var transfers = new Transfers(db);
        var timelines = new StockPositionTimelines(db);
        var lots = new StockReceiptLots(db);
        var tag = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var catalog = new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = tag, Variant = "original",
            Name = "Fictional quarantine fixture", ColorId = 0, ColorState = "known", Condition = "new",
            Completeness = "complete", Source = "Fictional reference", License = "operator-owned", Mapping = "confirmed"
        };
        var item = await references.Save(catalog, Guid.NewGuid(), token);
        async Task<(Guid Id, string Code)> Bin(string suffix)
        {
            var code = "QUARANTINE-" + tag + "-" + suffix;
            var result = await references.Save(new LocationCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code, Name = "Fictional bin"
            }, Guid.NewGuid(), token);
            return (result.EntityId, code);
        }
        var a = await Bin("A");
        var b = await Bin("B");
        var purchase = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "QUARANTINE-" + tag,
            Source = "Fictional supplier", Currency = "USD", OrderedQuantity = 10, PendingChargeMinor = 7
        }, Guid.NewGuid(), token);
        Task<Receipt> Deliver(int revision, string label, int quantity, int? cost) => receiving.Save(new DeliveryCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            Reference = label, Quantity = quantity, CatalogId = item.EntityId, ItemCostMinor = cost
        }, Guid.NewGuid(), token);
        Task<Receipt> Place(Receipt delivery, (Guid Id, string Code) bin, int quantity) => receiving.Save(new PutawayCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = delivery.Revision, PurchaseId = purchase.EntityId,
            DeliveryId = delivery.EntityId, CatalogId = item.EntityId, LocationId = bin.Id,
            ConfirmedLocationCode = bin.Code, Quantity = quantity
        }, Guid.NewGuid(), token);
        var zero = await Deliver(purchase.Revision, "Documented zero", 8, 0);
        var origin = await Place(zero, a, 8);
        var unknown = await Deliver(origin.Revision, "Unknown cost", 2, null);
        var sibling = await Place(unknown, b, 2);
        Task<Receipt> Offer(int revision, int quantity) => receiving.Save(new OfferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            CatalogId = item.EntityId, UnitPriceMinor = 249, Backing = [new(origin.EntityId, quantity)]
        }, Guid.NewGuid(), token);
        var offer = await Offer(sibling.Revision, 3);
        Task<Receipt> Hold(int revision, int quantity) => holds.Save(new StockHoldCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId, PositionId = origin.EntityId,
            LocationId = a.Id, ConfirmedLocationCode = a.Code, Quantity = quantity, Purpose = "Test model",
            Reason = "Hold witnessed build units"
        }, Guid.NewGuid(), token);
        var hold = await Hold(offer.Revision, 2);
        var add = new StockQuarantineCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = purchase.EntityId, ExpectedRevision = hold.Revision,
            PositionId = origin.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code, CatalogId = item.EntityId,
            CatalogRevision = item.Revision, Quantity = 2, Reason = "Inspect the surface finish"
        };
        Task<Receipt> Save(StockQuarantineCommand command) => quarantines.Save(command, Guid.NewGuid(), token);
        async Task<string> Reject(Func<Task> action) => (await Assert.ThrowsAsync<Fault>(action)).Code;
        Assert.Equal("quarantine_bin_conflict", await Reject(() => Save(add with { ConfirmedLocationCode = b.Code })));
        Assert.Equal("quarantine_quantity_unavailable", await Reject(() => Save(add with { Quantity = 4 })));
        Assert.Equal("quarantine_catalog_conflict", await Reject(() => Save(add with { CatalogRevision = item.Revision + 1 })));
        var duplicates = await Task.WhenAll(Save(add), Save(add));
        var receipt = duplicates[0];
        Assert.Equal(receipt, duplicates[1]);

        Task<StockQuarantineSnapshot> Snapshot(Guid? after = null, int limit = 25) =>
            quarantines.Snapshot(origin.EntityId, after, limit, token);
        var isolated = await Snapshot();
        Assert.Equal((8, 2, 2, 3, 4, 1), (isolated.OnHandQuantity, isolated.QuarantinedQuantity,
            isolated.HeldQuantity, isolated.OfferBackedQuantity, isolated.SellableQuantity, isolated.AvailableToQuarantineQuantity));
        Assert.Equal(0, isolated.Position.Layer.ReceiptItemCostMinor);
        Assert.Null(Assert.Single((await bins.Snapshot(b.Id, token)).Layers).ReceiptItemCostMinor);
        Assert.Equal("quarantine_capacity_unavailable", await Reject(() => Offer(receipt.Revision, 2)));
        Assert.Equal("quarantine_capacity_unavailable", await Reject(() => Hold(receipt.Revision, 2)));
        Assert.Equal("quarantine_capacity_unavailable", await Reject(() => transfers.Save(new TransferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = receipt.Revision, PurchaseId = purchase.EntityId,
            SourcePlacementId = origin.EntityId, SourceLocationId = a.Id, ConfirmedSourceLocationCode = a.Code,
            DestinationLocationId = b.Id, ConfirmedDestinationLocationCode = b.Code, Quantity = 2,
            Reason = "Must preserve quarantined units", Backing = []
        }, Guid.NewGuid(), token)));
        Assert.Equal("quarantine_client_upgrade_required", await Reject(() => bins.Movements(a.Id, token)));
        Assert.Equal("quarantine_client_upgrade_required", await Reject(() => timelines.Read(origin.EntityId, null, 25, token)));
        Assert.Equal("quarantine_client_upgrade_required", await Reject(() => lots.Read(zero.EntityId, null, null, 25, 25, token)));
        Assert.Equal(1, (await bins.Snapshot(a.Id, token)).FreeToBackQuantity);
        var activeLot = await lots.Read(zero.EntityId, null, null, 25, 25, token, quarantine: true);
        Assert.Equal((8, 2, 3, 2, 1), (activeLot.CurrentOnHandQuantity, activeLot.QuarantinedQuantity,
            activeLot.OfferBackedQuantity, activeLot.HeldQuantity, activeLot.FreeToBackQuantity));
        var purchaseState = Assert.Single((await receiving.LossSnapshot(token, true)).Purchases, p => p.Id == purchase.EntityId);
        Assert.Equal(2, purchaseState.Balances.Unavailable);
        Assert.Equal(2, purchaseState.Balances.Reserved);
        Assert.Equal(3, Assert.Single(purchaseState.Offers).BackedQuantity);
        Assert.Equal(2, Assert.Single(purchaseState.Deliveries, d => d.Id == zero.EntityId).QuarantinedQuantity);

        var drafts = new DraftBackingWithdrawals(db);
        Task<DraftBackingSnapshot> DraftSnapshot(bool quarantine = true) =>
            drafts.Snapshot(origin.EntityId, null, 25, token, quarantine);
        Task<DraftBackingHistory> DraftHistory(bool quarantine = true) =>
            drafts.History(origin.EntityId, offer.EntityId, null, 25, token, quarantine);
        var beforeWithdrawal = await DraftSnapshot();
        Assert.Equal((8, 2, 2, 4, 3, 1), (beforeWithdrawal.PhysicalOnHand, beforeWithdrawal.HeldQuantity,
            beforeWithdrawal.UnavailableQuantity, beforeWithdrawal.SellableQuantity, beforeWithdrawal.OfferBackedQuantity,
            beforeWithdrawal.WithheldQuantity));
        Assert.Equal(2, beforeWithdrawal.Position.Layer.QuarantinedQuantity);
        Assert.Equal(3, Assert.Single(beforeWithdrawal.Offers).BackedQuantity);
        Assert.Equal("quarantine_client_upgrade_required", await Reject(() => DraftSnapshot(false)));
        Assert.Equal("quarantine_client_upgrade_required", await Reject(() => DraftHistory(false)));

        var withdrawalRequest = new DraftBackingWithdrawalCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = purchase.EntityId, ExpectedRevision = receipt.Revision,
            PositionId = origin.EntityId, OfferId = offer.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code,
            Quantity = 1, Reason = "Withdraw one draft unit while quarantine remains"
        };
        var withdrawal = await drafts.Save(withdrawalRequest, Guid.NewGuid(), token);
        Assert.Equal(withdrawal, await drafts.Save(withdrawalRequest, Guid.NewGuid(), token));
        var afterWithdrawal = await DraftSnapshot();
        Assert.Equal((8, 2, 2, 4, 2, 2, 2), (afterWithdrawal.PhysicalOnHand, afterWithdrawal.HeldQuantity,
            afterWithdrawal.UnavailableQuantity, afterWithdrawal.SellableQuantity, afterWithdrawal.OfferBackedQuantity,
            afterWithdrawal.WithheldQuantity, afterWithdrawal.FreeToBackQuantity));
        Assert.Equal((2, 0), (afterWithdrawal.Position.Layer.QuarantinedQuantity, afterWithdrawal.Position.Layer.ReceiptItemCostMinor));
        Assert.Equal(withdrawal.Revision, afterWithdrawal.PurchaseRevision);
        var backedHistory = await DraftHistory();
        Assert.Equal(afterWithdrawal.Position, backedHistory.Position);
        Assert.Equal((2, 2, 4, 2), (backedHistory.UnavailableQuantity, backedHistory.OfferBackedQuantity,
            backedHistory.SellableQuantity, backedHistory.WithheldQuantity));
        Assert.Equal(Assert.Single(afterWithdrawal.Offers), backedHistory.Offer);
        Assert.Equal(new[] { -1, 3 }, backedHistory.Contributions.Select(row => row.BackingDelta));
        var withdrawalEvidence = await drafts.Evidence(origin.EntityId, null, withdrawal.CommandId, token);
        Assert.Equal((withdrawal.EntityId, offer.EntityId, "USD", 3, 2, 3, 2),
            (withdrawalEvidence.Id, withdrawalEvidence.OfferId, withdrawalEvidence.Currency, withdrawalEvidence.PriorBackedQuantity,
             withdrawalEvidence.RemainingBackedQuantity, withdrawalEvidence.PriorTotalOfferBackedQuantity,
             withdrawalEvidence.RemainingTotalOfferBackedQuantity));
        var quarantinedEvidence = await quarantines.Evidence(origin.EntityId, null, receipt.CommandId, token);
        Assert.Equal((receipt.EntityId, zero.EntityId, 2), (quarantinedEvidence.Id, quarantinedEvidence.DeliveryId, quarantinedEvidence.Quantity));

        Assert.Equal("draft_backing_client_upgrade_required", await Reject(() =>
            timelines.Read(origin.EntityId, null, 25, token, quarantine: true)));
        Assert.Equal("quarantine_client_upgrade_required", await Reject(() =>
            timelines.Read(origin.EntityId, null, 25, token, withdrawals: true)));
        Assert.Equal("draft_backing_client_upgrade_required", await Reject(() =>
            lots.Read(zero.EntityId, null, null, 25, 25, token, quarantine: true)));
        Assert.Equal("quarantine_client_upgrade_required", await Reject(() =>
            lots.Read(zero.EntityId, null, null, 25, 25, token, withdrawals: true)));
        var combinedTimeline = await timelines.Read(origin.EntityId, null, 1, token, withdrawals: true, quarantine: true);
        var withdrawEvent = Assert.Single(combinedTimeline.Events);
        Assert.Equal((withdrawal.EntityId, "offer-withdrawal", 0, 0, -1), (withdrawEvent.Id, withdrawEvent.Kind,
            withdrawEvent.PhysicalDelta, withdrawEvent.ReservedDelta, withdrawEvent.OfferBackedDelta));
        var nextEvent = await timelines.Read(origin.EntityId, combinedTimeline.NextAfter, 1, token, withdrawals: true, quarantine: true);
        Assert.Equal((receipt.EntityId, "quarantine"), (Assert.Single(nextEvent.Events).Id, nextEvent.Events[0].Kind));
        var combinedLot = await lots.Read(zero.EntityId, null, null, 25, 25, token, withdrawals: true, quarantine: true);
        Assert.Equal((8, 2, 2, 2, 0), (combinedLot.CurrentOnHandQuantity, combinedLot.QuarantinedQuantity,
            combinedLot.OfferBackedQuantity, combinedLot.HeldQuantity, combinedLot.ItemCostMinor));
        Assert.Contains(combinedLot.Events, row => row.Event.Id == withdrawal.EntityId && row.Event.OfferBackedDelta == -1);
        Assert.Contains(combinedLot.Events, row => row.Event.Id == receipt.EntityId && row.Event.PhysicalDelta == 0);

        var release = new StockQuarantineReleaseCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = purchase.EntityId, ExpectedRevision = withdrawal.Revision,
            PositionId = origin.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code, CatalogId = item.EntityId,
            CatalogRevision = item.Revision, Quantity = 1, QuarantineId = receipt.EntityId,
            InspectionResult = "passed", Reason = "Inspected one original piece"
        };
        Assert.Equal("invalid_quarantine", await Reject(() => Save(release with { InspectionResult = "failed" })));
        Assert.Equal("unknown_quarantine_receipt", await Reject(() => Save(release with { QuarantineId = Guid.NewGuid() })));
        Assert.Equal("quarantine_quantity_unavailable", await Reject(() => Save(release with { Quantity = 3 })));
        var released = await Save(release);
        await using (var sql = await db.OpenConnectionAsync(token))
        await using (var transaction = await sql.BeginTransactionAsync(token))
        {
            var forged = new Receipt(Guid.NewGuid(), Guid.NewGuid(), released.Revision + 1, "quarantine", Guid.NewGuid());
            await CommandJournal.Append(sql, transaction, forged, add.Digest(), "record-receiving", token);
            await using var direct = Database.Query(sql, transaction, """
                INSERT INTO brickvault.stock_quarantines(id,command_id,purchase_id,position_id,location_id,location_code,
                    catalog_id,catalog_revision,expected_purchase_revision,quantity,reason)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,1,'Mismatched fixture receipt')
                """, Guid.NewGuid(), forged.CommandId, purchase.EntityId, origin.EntityId, a.Id, a.Code, item.EntityId,
                item.Revision, released.Revision);
            Assert.Equal(1, await direct.ExecuteNonQueryAsync(token));
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync(token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
            Assert.Equal("quarantine_receipt_required", rejected.ConstraintName);
        }
        var partial = await Snapshot();
        Assert.Equal((8, 1, 2, 2, 5, 3), (partial.OnHandQuantity, partial.QuarantinedQuantity, partial.HeldQuantity,
            partial.OfferBackedQuantity, partial.SellableQuantity, partial.AvailableToQuarantineQuantity));
        Assert.Equal(released.Revision, partial.PurchaseRevision);
        Assert.Equal(1, Assert.Single(partial.Quarantines).RemainingQuantity);
        Assert.Equal(released, await Save(release));
        var afterRelease = await DraftHistory();
        Assert.Equal((8, 1, 2, 5, 3), (afterRelease.PhysicalOnHand, afterRelease.UnavailableQuantity,
            afterRelease.Offer.BackedQuantity, afterRelease.SellableQuantity, afterRelease.WithheldQuantity));
        Assert.Equal(backedHistory.Contributions, afterRelease.Contributions);
        var retainedWithdrawal = await drafts.Evidence(origin.EntityId, withdrawal.EntityId, null, token);
        Assert.Equal(withdrawalEvidence with { AsOf = retainedWithdrawal.AsOf }, retainedWithdrawal);

        var rivals = Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                return (Receipt: await Save(add with { CommandId = Guid.NewGuid(), ExpectedRevision = released.Revision }), Error: "");
            }
            catch (Fault error)
            {
                return (Receipt: (Receipt?)null, Error: error.Code);
            }
        });
        var competing = await Task.WhenAll(rivals);
        var seized = Assert.Single(competing, r => r.Receipt is not null).Receipt!;
        Assert.Equal("revision_conflict", Assert.Single(competing, r => r.Receipt is null).Error);
        var first = await Snapshot(limit: 1);
        Assert.Equal(seized.EntityId, first.NextAfter);
        var second = await Snapshot(first.NextAfter, 1);
        Assert.Equal(receipt.EntityId, Assert.Single(second.Quarantines).Id);
        Assert.Null(second.NextAfter);
        Assert.Equal("invalid_quarantine_cursor",
            await Reject(() => quarantines.Snapshot(sibling.EntityId, first.NextAfter, 1, token)));

        // A separate normal allocation uses the newly free unit, keeping the shortage example at three backed units.
        var additionalOffer = await Offer(seized.Revision, 1);
        var edited = await references.Save(catalog with
        {
            CommandId = Guid.NewGuid(), Id = item.EntityId, ExpectedRevision = item.Revision,
            Name = "Fictional edited catalog", Condition = "used", Variant = "edited", Completeness = "incomplete"
        }, Guid.NewGuid(), token);
        var evidence = await quarantines.Evidence(origin.EntityId, released.EntityId, null, token);
        Assert.Equal(release.CommandId, evidence.CommandId);
        Assert.Equal(released.CorrelationId, evidence.CorrelationId);
        Assert.Equal(receipt.EntityId, evidence.QuarantineId);
        Assert.Equal("passed", evidence.InspectionResult);
        Assert.Equal(item.Revision, evidence.RecordedCatalog.Revision);
        Assert.Equal("new", evidence.RecordedCatalog.Condition);
        Assert.Equal("complete", evidence.RecordedCatalog.Completeness);
        Assert.Equal("original", evidence.RecordedCatalog.Variant);
        Assert.Equal("unknown_quarantine_receipt",
            await Reject(() => quarantines.Evidence(sibling.EntityId, null, release.CommandId, token)));
        var timeline = await timelines.Read(origin.EntityId, null, 25, token, withdrawals: true, quarantine: true);
        var physicalRelease = Assert.Single(timeline.Events, e => e.Id == released.EntityId);
        Assert.Equal("quarantine-release", physicalRelease.Kind);
        Assert.Equal((0, 0, 0), (physicalRelease.PhysicalDelta, physicalRelease.ReservedDelta, physicalRelease.OfferBackedDelta));
        var inspected = await Save(release with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = additionalOffer.Revision, CatalogRevision = edited.Revision,
            Reason = "Inspected a piece from the second quarantine", QuarantineId = seized.EntityId
        });
        var nextEvidence = await quarantines.Evidence(origin.EntityId, null, inspected.CommandId, token);
        Assert.Equal("used", nextEvidence.RecordedCatalog.Condition);
        Assert.Equal(item.Revision, (await Snapshot()).Position.Layer.CatalogRevision);

        var counts = new StockCounts(db);
        var observation = new StockCountCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = purchase.EntityId, ExpectedRevision = inspected.Revision,
            PositionId = origin.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code, CountedQuantity = 6,
            Reason = "Aggregate witness does not identify quarantine pieces"
        };
        var count = await counts.Save(observation, Guid.NewGuid(), token);
        var adjustments = new StockAdjustmentReviews(db);
        Task<StockAdjustmentReview> Review(bool includeQuarantine = true) =>
            adjustments.Read(a.Id, origin.EntityId, count.EntityId, null, 25, token, includeQuarantine);
        var preview = await Review();
        Assert.True(preview.QuarantineBlocksLoss);
        Assert.Equal((8, 6, 2, 2, 3, 2), (preview.LedgerQuantity, preview.LatestObservation.CountedQuantity,
            preview.CandidateLossQuantity, preview.ProtectedProjectQuantity, preview.OfferBackedQuantity,
            preview.QuarantinedQuantity));
        Assert.Null(preview.OfferCapacityAfterLoss);
        Assert.Null(preview.MinimumOfferBackingReduction);
        var wire = JsonSerializer.SerializeToElement(preview, Wire.Json);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("offerCapacityAfterLoss").ValueKind);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("minimumOfferBackingReduction").ValueKind);
        Assert.Equal("quarantine_client_upgrade_required", await Reject(() => Review(false)));
        Assert.Equal("stock_loss_quarantine_unresolved", await Reject(() => new StockLosses(db).Save(new StockLossCommand
        {
            CommandId = Guid.NewGuid(), PurchaseId = purchase.EntityId, ExpectedRevision = count.Revision,
            PositionId = origin.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code, CaseId = count.EntityId,
            ExpectedObservationId = count.EntityId, Reason = "Aggregate count cannot identify missing quarantined pieces",
            OfferReductions = []
        }, Guid.NewGuid(), token)));
        Assert.Equal("quarantine_shortage_open", await Reject(() => Save(release with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = count.Revision, CatalogRevision = edited.Revision
        })));
        var stillPresent = await Snapshot();
        Assert.Equal((8, 2, 2, 3, 0), (stillPresent.OnHandQuantity, stillPresent.QuarantinedQuantity,
            stillPresent.HeldQuantity, stillPresent.OfferBackedQuantity, stillPresent.AvailableToQuarantineQuantity));

        var revision = count.Revision;
        foreach (var quantity in new[] { 8, 9 })
        {
            var recount = await counts.Save(observation with
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = revision, CountedQuantity = quantity
            }, Guid.NewGuid(), token);
            revision = recount.Revision;
            var noLoss = await Review();
            Assert.Equal((quantity, 0, 4, 0), (noLoss.LatestObservation.CountedQuantity, noLoss.CandidateLossQuantity,
                noLoss.OfferCapacityAfterLoss, noLoss.MinimumOfferBackingReduction));
        }
    }
}
