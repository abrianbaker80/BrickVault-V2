using System.Text.Json;
using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

[Collection("Build holds")]
public sealed class StockShortageClosureTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task ExactRecountClosesOnlyItsOpenCaseAndRetainsStockAndWitnessEvidence()
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
        var counts = new StockCounts(db);
        var closures = new StockShortageClosures(db);
        var quarantines = new StockQuarantines(db);
        var losses = new StockLosses(db);
        var timelines = new StockPositionTimelines(db);
        var lots = new StockReceiptLots(db);
        var tag = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var item = await references.Save(new CatalogCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, ItemType = "part", ItemNumber = tag, Variant = null,
            Name = "Fictional closure fixture", ColorId = 0, ColorState = "known", Condition = "new",
            Completeness = "complete", Source = "Fictional reference", License = "operator-owned", Mapping = "confirmed"
        }, Guid.NewGuid(), token);
        async Task<(Guid Id, string Code)> Register(string suffix)
        {
            var code = $"CLOSURE-{tag}-{suffix}";
            var location = await references.Save(new LocationCommand
            {
                CommandId = Guid.NewGuid(), ExpectedRevision = 0, Code = code, Name = "Fictional bin"
            }, Guid.NewGuid(), token);
            return (location.EntityId, code);
        }
        var a = await Register("A");
        var b = await Register("B");
        var purchase = await receiving.Save(new PurchaseCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = 0, Reference = "CLOSURE-" + tag,
            Source = "Fictional supplier", Currency = "USD", OrderedQuantity = 11, PendingChargeMinor = null
        }, Guid.NewGuid(), token);
        var delivery = await receiving.Save(new DeliveryCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = purchase.Revision, PurchaseId = purchase.EntityId,
            Reference = "Documented fixture cost", Quantity = 11, CatalogId = item.EntityId, ItemCostMinor = 103
        }, Guid.NewGuid(), token);
        Task<Receipt> Place(int revision, (Guid Id, string Code) bin, int quantity) => receiving.Save(new PutawayCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId,
            DeliveryId = delivery.EntityId, CatalogId = item.EntityId, LocationId = bin.Id,
            ConfirmedLocationCode = bin.Code, Quantity = quantity
        }, Guid.NewGuid(), token);
        var position = await Place(delivery.Revision, a, 8);
        var sibling = await Place(position.Revision, b, 3);
        var offer = await receiving.Save(new OfferCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = sibling.Revision, PurchaseId = purchase.EntityId,
            CatalogId = item.EntityId, UnitPriceMinor = 240, Backing = [new(position.EntityId, 3), new(sibling.EntityId, 1)]
        }, Guid.NewGuid(), token);
        var held = await new BuildHolds(db).Save(new StockHoldCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = offer.Revision, PurchaseId = purchase.EntityId,
            PositionId = position.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code,
            Purpose = "Fictional build", Quantity = 2, Reason = "Retain project stock"
        }, Guid.NewGuid(), token);
        var quarantined = await quarantines.Save(new StockQuarantineCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = held.Revision, PurchaseId = purchase.EntityId,
            PositionId = position.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code, CatalogId = item.EntityId,
            CatalogRevision = item.Revision, Quantity = 2, Reason = "Inspect surface quality"
        }, Guid.NewGuid(), token);

        Task<Receipt> Witness(int revision, Receipt stock, (Guid Id, string Code) bin, int quantity) => counts.Save(new StockCountCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = revision, PurchaseId = purchase.EntityId, PositionId = stock.EntityId,
            LocationId = bin.Id, ConfirmedLocationCode = bin.Code, CountedQuantity = quantity, Reason = "Fictional witnessed count"
        }, Guid.NewGuid(), token);
        async Task<string> Reject(Func<Task> action) => (await Assert.ThrowsAsync<Fault>(action)).Code;
        Task<Receipt> Save(StockShortageClosureCommand request) => closures.Save(request, Guid.NewGuid(), token);
        Task<StockShortageClosureEvidence> Read(Guid caseId, Guid? commandId = null) =>
            closures.Read(a.Id, position.EntityId, caseId, commandId, token);
        Task<BinCountSnapshot> ReadCount(Guid observationId, bool include = true) =>
            counts.Snapshot(a.Id, token, position.EntityId, observationId: observationId, closures: include);
        async Task<LossPurchaseRow> PurchaseState() =>
            Assert.Single((await receiving.LossSnapshot(token, quarantine: true, recoveries: true)).Purchases, row => row.Id == purchase.EntityId);

        var opening = await Witness(quarantined.Revision, position, a, 6);
        var command = new StockShortageClosureCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = opening.Revision, PurchaseId = purchase.EntityId,
            PositionId = position.EntityId, LocationId = a.Id, ConfirmedLocationCode = a.Code, CaseId = opening.EntityId,
            ExpectedObservationId = opening.EntityId, Reason = "Later exact count confirms original stock"
        };
        Assert.Equal("shortage_closure_count_not_exact", await Reject(() => Save(command)));
        Assert.Equal("unknown_shortage_closure", await Reject(() => Read(opening.EntityId)));
        Assert.Empty((await ReadCount(opening.EntityId)).Closures!);
        var intermediate = await Witness(opening.Revision, position, a, 7);
        var olderEqual = await Witness(intermediate.Revision, position, a, 8);
        Assert.Single((await ReadCount(olderEqual.EntityId)).Shortages);
        var surplus = await Witness(olderEqual.Revision, position, a, 9);
        Assert.Equal("shortage_closure_observation_conflict", await Reject(() =>
            Save(command with { ExpectedRevision = surplus.Revision, ExpectedObservationId = olderEqual.EntityId })));
        Assert.Equal("shortage_closure_count_not_exact", await Reject(() =>
            Save(command with { ExpectedRevision = surplus.Revision, ExpectedObservationId = surplus.EntityId })));
        var latest = await Witness(surplus.Revision, position, a, 8);
        var otherCount = await Witness(latest.Revision, sibling, b, 3);
        Assert.Equal("revision_conflict", await Reject(() =>
            Save(command with { ExpectedRevision = latest.Revision, ExpectedObservationId = latest.EntityId })));
        command = command with { ExpectedRevision = otherCount.Revision, ExpectedObservationId = latest.EntityId };
        Assert.Equal("shortage_closure_case_conflict", await Reject(() => Save(command with { CaseId = Guid.NewGuid() })));
        Assert.Equal("shortage_closure_bin_conflict", await Reject(() => Save(command with { ConfirmedLocationCode = b.Code })));
        Assert.Equal("shortage_closure_position_mismatch", await Reject(() => Save(command with { LocationId = b.Id })));
        Assert.Equal("shortage_closure_observation_conflict", await Reject(() =>
            Save(command with { ExpectedObservationId = otherCount.EntityId })));
        Assert.Equal("invalid_shortage_closure", await Reject(() => Save(command with { Reason = " " })));

        // The application role cannot advance the case with a closure that lacks its matching durable receipt.
        await using (var sql = await db.OpenConnectionAsync(token))
        await using (var tx = await sql.BeginTransactionAsync(token))
        {
            var wrongReceipt = new Receipt(Guid.NewGuid(), Guid.NewGuid(), command.ExpectedRevision + 1,
                "shortage-closure", Guid.NewGuid());
            await CommandJournal.Append(sql, tx, wrongReceipt, command.Digest(), "record-receiving", token);
            await using var insert = Database.Query(sql, tx, """
                INSERT INTO brickvault.stock_shortage_closures(id,command_id,purchase_id,position_id,location_id,location_code,
                    case_id,observation_id,expected_purchase_revision,reason)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                """, Guid.NewGuid(), wrongReceipt.CommandId, purchase.EntityId, position.EntityId, a.Id, a.Code,
                opening.EntityId, latest.EntityId, command.ExpectedRevision, command.Reason);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync(token));
            Assert.Equal("shortage_closure_receipt_required", rejected.ConstraintName);
        }
        var before = await PurchaseState();
        Assert.Equal(command.ExpectedRevision, before.Revision);
        Assert.Single((await ReadCount(latest.EntityId)).Shortages);
        var alternative = command with { CommandId = Guid.NewGuid() };
        async Task<(Receipt? Receipt, StockShortageClosureCommand Request, string? Error)> Rival(StockShortageClosureCommand attempt)
        {
            try { return (await Save(attempt), attempt, null); }
            catch (Fault error) { return (null, attempt, error.Code); }
        }
        var rivals = await Task.WhenAll(Rival(command), Rival(alternative));
        var winner = Assert.Single(rivals, row => row.Receipt is not null);
        var saved = winner.Receipt!;
        Assert.Equal("revision_conflict", Assert.Single(rivals, row => row.Receipt is null).Error);
        Assert.Equal(saved, await Save(winner.Request));
        Assert.Equal("command_mismatch", await Reject(() => Save(winner.Request with { Reason = "Different confirmation" })));
        Assert.Equal("shortage_closure_case_conflict", await Reject(() =>
            Save(command with { CommandId = Guid.NewGuid(), ExpectedRevision = saved.Revision })));

        var exact = await Read(opening.EntityId, saved.CommandId);
        Assert.Equal((saved.EntityId, saved.CorrelationId, saved.Revision, opening.EntityId, latest.EntityId),
            (exact.Id, exact.CorrelationId, exact.Revision, exact.OpeningObservation.Id, exact.RecountObservation.Id));
        Assert.Equal((8, 2, 2, 3, 6, 8, 103), (exact.LedgerQuantity, exact.QuarantinedQuantity, exact.HeldQuantity,
            exact.OfferBackedQuantity, exact.OpeningObservation.CountedQuantity, exact.RecountObservation.CountedQuantity,
            exact.Receipt.ItemCostMinor));
        Assert.False(exact.OpeningObservation.PostedAdjustment);
        Assert.False(exact.RecountObservation.PostedAdjustment);
        Assert.Equal("unknown_shortage_closure", await Reject(() => Read(opening.EntityId, Guid.NewGuid())));
        Assert.Equal("unknown_shortage_closure", await Reject(() => closures.Read(b.Id, position.EntityId, opening.EntityId, null, token)));
        Assert.Equal("unknown_stock_loss", await Reject(() => losses.Read(a.Id, position.EntityId, opening.EntityId, null, null, null, null, 25, token)));

        var after = await PurchaseState();
        Assert.Equal(before.Balances, after.Balances);
        var oldOffer = Assert.Single(before.Offers);
        var afterOffer = Assert.Single(after.Offers);
        Assert.Equal(oldOffer with { Backing = afterOffer.Backing }, afterOffer);
        Assert.Equal(oldOffer.Backing, afterOffer.Backing);
        var deliveryAfter = Assert.Single(after.Deliveries);
        Assert.Equal((103, 0, 2), (deliveryAfter.RemainingItemCostMinor, deliveryAfter.LostQuantity, deliveryAfter.QuarantinedQuantity));
        Assert.Equal(1, Assert.Single(deliveryAfter.Placements, row => row.Id == position.EntityId).FreeToBackQuantity);
        Assert.Equal(103, exact.Receipt.ItemCostMinor);

        foreach (var observation in new[] { opening, intermediate, olderEqual, surplus, latest })
        {
            var page = await ReadCount(observation.EntityId);
            Assert.Empty(page.Shortages);
            var linked = Assert.Single(page.Closures!);
            Assert.Equal((saved.EntityId, opening.EntityId, latest.EntityId),
                (linked.Id, linked.OpeningObservationId, linked.RecountObservationId));
            Assert.False(Assert.Single(page.Observations).PostedAdjustment);
        }
        var legacy = await ReadCount(latest.EntityId, false);
        Assert.Empty(legacy.Shortages);
        Assert.False(JsonSerializer.SerializeToElement(legacy, Wire.Json).TryGetProperty("closures", out _));
        Assert.Equal("shortage_closure_client_upgrade_required", await Reject(() =>
            timelines.Read(position.EntityId, null, 1, token, quarantine: true)));
        Assert.Equal("shortage_closure_client_upgrade_required", await Reject(() =>
            lots.Read(delivery.EntityId, null, null, 25, 1, token, quarantine: true)));
        var pageOne = await timelines.Read(position.EntityId, null, 1, token, quarantine: true, closures: true);
        var closedEvent = Assert.Single(pageOne.Events);
        Assert.Equal(("shortage-closure", saved.EntityId, latest.EntityId, opening.EntityId, 8, 0, 0, 0),
            (closedEvent.Kind, closedEvent.Id, closedEvent.ObservationId, closedEvent.CaseId, closedEvent.Quantity,
                closedEvent.PhysicalDelta, closedEvent.ReservedDelta, closedEvent.OfferBackedDelta));
        var closureCursor = Assert.IsType<string>(pageOne.NextAfter);
        using var contractStream = typeof(StockPositionTimeline).Assembly
            .GetManifestResourceStream("BrickVault.Api.openapi.v1.json")!;
        using var contract = JsonDocument.Parse(contractStream);
        var timelineContract = contract.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/inventory/positions/{positionId}/timeline").GetProperty("get");
        var input = timelineContract.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.TryGetProperty("name",out var name) && name.GetString() == "after").GetProperty("schema");
        var output = contract.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("StockPositionTimeline").GetProperty("properties").GetProperty("nextAfter");
        var pattern = input.GetProperty("pattern").GetString()!;
        Assert.Equal(pattern, output.GetProperty("pattern").GetString());
        Assert.Equal(input.GetProperty("maxLength").GetInt32(), output.GetProperty("maxLength").GetInt32());
        Assert.True(closureCursor.Length <= input.GetProperty("maxLength").GetInt32());
        Assert.Matches(pattern, closureCursor);
        var next = await timelines.Read(position.EntityId, closureCursor, 1, token, quarantine: true, closures: true);
        Assert.Equal(latest.EntityId, Assert.Single(next.Events).Id);
        Assert.Equal("invalid_timeline_cursor", await Reject(() =>
            timelines.Read(sibling.EntityId, closureCursor, 1, token, closures: true)));
        var lotBeforeAnyLoss = await lots.Read(delivery.EntityId, null, null, 25, 25, token, quarantine: true, closures: true);
        Assert.Equal((11, 0, 2, 2, 4, 103), (lotBeforeAnyLoss.CurrentOnHandQuantity, lotBeforeAnyLoss.LostQuantity,
            lotBeforeAnyLoss.QuarantinedQuantity, lotBeforeAnyLoss.HeldQuantity, lotBeforeAnyLoss.OfferBackedQuantity,
            lotBeforeAnyLoss.RemainingItemCostMinor));
        Assert.Contains(lotBeforeAnyLoss.Events, row => row.Event.Id == saved.EntityId && row.Event.PhysicalDelta == 0);

        var future = await Witness(saved.Revision, position, a, 5);
        var recurrence = await ReadCount(future.EntityId);
        Assert.Equal(future.EntityId, Assert.Single(recurrence.Shortages).Id);
        Assert.Empty(recurrence.Closures!);
        Assert.Empty((await ReadCount(opening.EntityId)).Shortages);
        var retained = await Read(opening.EntityId);
        Assert.Equal(exact with { AsOf = retained.AsOf }, retained);

        var otherOpening = await Witness(future.Revision, sibling, b, 2);
        var posted = await losses.Save(new StockLossCommand
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = otherOpening.Revision, PurchaseId = purchase.EntityId,
            PositionId = sibling.EntityId, LocationId = b.Id, ConfirmedLocationCode = b.Code, CaseId = otherOpening.EntityId,
            ExpectedObservationId = otherOpening.EntityId, Reason = "Fictional sibling loss", OfferReductions = []
        }, Guid.NewGuid(), token);
        var otherExact = await Witness(posted.Revision, sibling, b, 2);
        Assert.Equal("shortage_closure_case_conflict", await Reject(() => Save(command with
        {
            CommandId = Guid.NewGuid(), ExpectedRevision = otherExact.Revision, PositionId = sibling.EntityId,
            LocationId = b.Id, ConfirmedLocationCode = b.Code, CaseId = otherOpening.EntityId, ExpectedObservationId = otherExact.EntityId
        })));
        Assert.Equal("unknown_shortage_closure",
            await Reject(() => closures.Read(b.Id, sibling.EntityId, otherOpening.EntityId, null, token)));
    }
}
