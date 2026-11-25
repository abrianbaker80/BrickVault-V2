using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

[Collection("Build holds")]
public sealed class StockLossRecoveryTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    [InlineData(null)]
    [Trait("Requires", "PostgreSQL")]
    public async Task NamedLossBasisReopensItsOwnSlotsAndFutureLossReusesThem(int? receiptCost)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(3, receiptCost, token);
        var first = await fixture.Lose(fixture.Position.Revision, 2);
        var second = await fixture.Lose(first.Revision, 0);
        var firstHistory = await fixture.ReadLoss(first.EntityId);
        var secondHistory = await fixture.ReadLoss(second.EntityId);
        Assert.Equal(receiptCost is null ? null : receiptCost/3, firstHistory.AllocatedItemCostMinor);
        Assert.Equal(receiptCost is null ? null : receiptCost-receiptCost/3, secondHistory.AllocatedItemCostMinor);
        Assert.Equal(receiptCost is null ? null : 0, secondHistory.RemainingDeliveryItemCostMinor);

        var restoredFirst = await fixture.Recoveries.Save(fixture.Prepare(second.Revision, first.EntityId, new StockLossRecoverySelection(null,1,0)),
            Guid.NewGuid(), token);
        var firstReturn = await fixture.Evidence(first.EntityId, restoredFirst);
        Assert.Equal(firstHistory.AllocatedItemCostMinor, firstReturn.RestoredItemCostMinor);
        var recoveredLot = await fixture.Lot();
        Assert.Equal((1,2), (recoveredLot.CurrentOnHandQuantity,recoveredLot.LostQuantity));
        Assert.Equal(secondHistory.AllocatedItemCostMinor,recoveredLot.AllocatedLostItemCostMinor);
        Assert.Equal(receiptCost/3,recoveredLot.RemainingItemCostMinor);
        Assert.Equal("stock_loss_recovery_client_upgrade_required", await Reject(() =>
            fixture.Lots.Read(fixture.Delivery.EntityId,null,null,25,25,token,quarantine:true)));
        Assert.Equal("stock_loss_recovery_amount", await Reject(() =>
            fixture.Recoveries.Save(fixture.Prepare(restoredFirst.Revision,first.EntityId,new StockLossRecoverySelection(null,1,0)),Guid.NewGuid(),token)));

        var third = await fixture.Lose(restoredFirst.Revision, 0);
        Assert.Equal("stock_loss_recovery_client_upgrade_required", await Reject(() => fixture.ReadLoss(third.EntityId)));
        var thirdHistory = await fixture.ReadLoss(third.EntityId, true);
        Assert.Equal("active",thirdHistory.PriorDeliveryLossQuantityMode);
        Assert.Equal(2,thirdHistory.PriorDeliveryLossQuantity);
        Assert.Equal(firstHistory.AllocatedItemCostMinor,thirdHistory.AllocatedItemCostMinor);
        Assert.Equal(receiptCost is null ? null : 0,thirdHistory.RemainingDeliveryItemCostMinor);

        var restoreSecond = fixture.Prepare(third.Revision,second.EntityId,new StockLossRecoverySelection(null,1,1));
        var firstRace = restoreSecond with { Allocations=[new(null,0,1)] };
        var firstTwo = await Task.WhenAll(fixture.Recoveries.Save(firstRace,Guid.NewGuid(),token),
            fixture.Recoveries.Save(firstRace,Guid.NewGuid(),token));
        Assert.Equal(firstTwo[0], firstTwo[1]);
        var receipt = firstTwo[0];
        var returnEvidence = await fixture.Evidence(second.EntityId, receipt);
        Assert.Equal(receiptCost is null ? null : receiptCost*2/3-receiptCost/3,returnEvidence.RestoredItemCostMinor);
        Assert.Equal(receipt.EntityId,returnEvidence.RecoveredQuarantineId);
        var revised = fixture.Prepare(receipt.Revision,second.EntityId,new StockLossRecoverySelection(null,1,0));
        async Task<(Receipt? Receipt,string? Fault)> Rival(StockLossRecoveryCommand c)
        {
            try { return (await fixture.Recoveries.Save(c,Guid.NewGuid(),token),null); }
            catch (Fault fault) { return (null,fault.Code); }
        }
        var rivals = await Task.WhenAll(Rival(revised),Rival(revised with {CommandId=Guid.NewGuid()}));
        var winning = Assert.Single(rivals, row => row.Receipt is not null).Receipt!;
        Assert.Equal("revision_conflict",Assert.Single(rivals,row=>row.Receipt is null).Fault);
        var secondReturn = await fixture.Evidence(second.EntityId,winning);
        Assert.Equal(receiptCost is null ? null : receiptCost-receiptCost*2/3,secondReturn.RestoredItemCostMinor);
        var retained = await fixture.ReadLoss(second.EntityId);
        Assert.Equal(secondHistory with
        {
            AsOf=retained.AsOf,Reductions=retained.Reductions,QuarantineAttributions=retained.QuarantineAttributions
        },retained);
        Assert.Empty(retained.QuarantineAttributions!);
        var lot = await fixture.Lot();
        Assert.Equal((2,1,1), (lot.CurrentOnHandQuantity,lot.LostQuantity,lot.QuarantinedQuantity));
        Assert.Equal(receiptCost/3,lot.AllocatedLostItemCostMinor);
        Assert.Equal((4L,3L),(lot.RecoveryTotals!.GrossLostQuantity,lot.RecoveryTotals.RecoveredQuantity));

        var review = await fixture.Recoveries.Review(fixture.Bin.EntityId,fixture.Position.EntityId,second.EntityId,
            null,1,null,1,token);
        Assert.Equal((2,2,0),(review.OriginalLostQuantity,review.RecoveredQuantity,review.RemainingQuantity));
        Assert.Equal(secondHistory.AllocatedItemCostMinor,review.RestoredItemCostMinor);
        Assert.Equal(receiptCost is null ? null : 0,review.RemainingLostItemCostMinor);
        Assert.Equal(winning.EntityId,Assert.Single(review.Recoveries).Id);
        var older = await fixture.Recoveries.Review(fixture.Bin.EntityId,fixture.Position.EntityId,second.EntityId,null,1,
            review.NextHistoryAfter,1,token);
        Assert.Equal(receipt.EntityId,Assert.Single(older.Recoveries).Id);
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task FutureLossSlicesAcrossSeparatedReopenedSlots()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(3,100,token);
        var first = await fixture.Lose(fixture.Position.Revision,2);
        var middle = await fixture.Lose(first.Revision,1);
        var last = await fixture.Lose(middle.Revision,0);
        var restoredFirst = await fixture.Recoveries.Save(fixture.Prepare(last.Revision,first.EntityId,
            new StockLossRecoverySelection(null,1,0)),Guid.NewGuid(),token);
        var restoredLast = await fixture.Recoveries.Save(fixture.Prepare(restoredFirst.Revision,last.EntityId,
            new StockLossRecoverySelection(null,1,0)),Guid.NewGuid(),token);
        var fragmented = await fixture.Lose(restoredLast.Revision,0);
        Assert.Equal(67,(await fixture.ReadLoss(fragmented.EntityId,true)).AllocatedItemCostMinor);
        await using (var connection = await fixture.Db.OpenConnectionAsync(token))
        {
            await using var query = Database.Query(connection,null,"""
                SELECT basis_slots::text FROM brickvault.stock_loss_basis_assignments WHERE loss_id=$1
                """,fragmented.EntityId);
            Assert.Equal("{[0,1),[2,3)}",await query.ExecuteScalarAsync(token));
        }
        var firstPartial = await fixture.Recoveries.Save(fixture.Prepare(fragmented.Revision,fragmented.EntityId,
            new StockLossRecoverySelection(null,1,0)),Guid.NewGuid(),token);
        var secondPartial = await fixture.Recoveries.Save(fixture.Prepare(firstPartial.Revision,fragmented.EntityId,
            new StockLossRecoverySelection(null,1,0)),Guid.NewGuid(),token);
        Assert.Equal(33,(await fixture.Evidence(fragmented.EntityId,firstPartial)).RestoredItemCostMinor);
        Assert.Equal(34,(await fixture.Evidence(fragmented.EntityId,secondPartial)).RestoredItemCostMinor);
        var net = await fixture.Lot();
        Assert.Equal((2,1,33,67),(net.CurrentOnHandQuantity,net.LostQuantity,net.AllocatedLostItemCostMinor,net.RemainingItemCostMinor));
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task MixedSourcesRequireExactIdentificationAndProduceAReleasableQuarantineWithoutClosingNewerCases()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(8,803,token);
        var offer = await new Receiving(fixture.Db).Save(new OfferCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=fixture.Position.Revision,PurchaseId=fixture.Purchase.EntityId,
            CatalogId=fixture.Item.EntityId,UnitPriceMinor=199,Backing=[new(fixture.Position.EntityId,3)]
        },Guid.NewGuid(),token);
        var hold = await new BuildHolds(fixture.Db).Save(new StockHoldCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=offer.Revision,PurchaseId=fixture.Purchase.EntityId,
            PositionId=fixture.Position.EntityId,LocationId=fixture.Bin.EntityId,ConfirmedLocationCode=fixture.Code,
            Quantity=2,Reason="Fictional build",Purpose="Model"
        },Guid.NewGuid(),token);
        var add = new StockQuarantineCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=hold.Revision,PurchaseId=fixture.Purchase.EntityId,PositionId=fixture.Position.EntityId,
            LocationId=fixture.Bin.EntityId,ConfirmedLocationCode=fixture.Code,CatalogId=fixture.Item.EntityId,
            CatalogRevision=fixture.Item.Revision,Quantity=1,Reason="Fictional rough surface"
        };
        var firstMark = await fixture.Quarantines.Save(add,Guid.NewGuid(),token);
        var secondMark = await fixture.Quarantines.Save(add with
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=firstMark.Revision,Reason="Fictional damaged edge"
        },Guid.NewGuid(),token);
        var lost = await fixture.Lose(secondMark.Revision,5,[new(firstMark.EntityId,1),new(secondMark.EntityId,1)]);
        var later = await fixture.Witness(lost.Revision,4);
        var review = await fixture.Recoveries.Review(fixture.Bin.EntityId,fixture.Position.EntityId,lost.EntityId,null,1,null,25,token);
        Assert.True(review.HasOpenShortage);
        Assert.Equal(later.Revision,review.PurchaseRevision);
        Assert.Null(Assert.Single(review.Sources).QuarantineId);
        var sourceCursor=Assert.IsType<string>(review.NextAfter);
        var named = await fixture.Recoveries.Review(fixture.Bin.EntityId,fixture.Position.EntityId,lost.EntityId,sourceCursor,25,null,25,token);
        Assert.Equal(2,named.Sources.Count);
        var selected = Assert.Single(named.Sources,row=>row.QuarantineId==firstMark.EntityId);
        Assert.Equal(add.Reason,selected.OriginalQuarantineReason);
        var command = fixture.Prepare(later.Revision,lost.EntityId,new(null,0,1),new(firstMark.EntityId,1,0));
        Task<Receipt> Save(StockLossRecoveryCommand c) => fixture.Recoveries.Save(c,Guid.NewGuid(),token);
        Assert.Equal("stock_loss_recovery_scope",await Reject(() => Save(command with {PostingId=Guid.NewGuid()})));
        Assert.Equal("stock_loss_recovery_bin",await Reject(() => Save(command with {ConfirmedLocationCode="OTHER"})));
        Assert.Equal("stock_loss_recovery_catalog",await Reject(() => Save(command with {CatalogRevision=fixture.Item.Revision+1})));
        Assert.Equal("invalid_stock_loss_recovery",await Reject(() => Save(command with {ConfirmedIdentity=false})));
        Assert.Equal("invalid_stock_loss_recovery",await Reject(() => Save(command with
        {
            Allocations=[new(null,0,1),new(null,1,0)]
        })));
        Assert.Equal("stock_loss_recovery_amount",await Reject(() => Save(command with {Allocations=[new(firstMark.EntityId,2,0)]})));
        var persisted = await Save(command);
        Assert.Equal(persisted,await Save(command with {Allocations=[..command.Allocations.Reverse()]}));
        Assert.Equal("stale_stock_loss_recovery_page",await Reject(() =>
            fixture.Recoveries.Review(fixture.Bin.EntityId,fixture.Position.EntityId,lost.EntityId,sourceCursor,25,null,25,token)));
        var evidence = await fixture.Evidence(lost.EntityId,persisted,limit:1);
        Assert.Equal((1,1,fixture.Delivery.EntityId,100+selected.OriginalItemCostMinor),
            (evidence.SaleableQuantity,evidence.QuarantineQuantity,evidence.Receipt.DeliveryId,evidence.RestoredItemCostMinor));
        Assert.Null(Assert.Single(evidence.Allocations).QuarantineId);
        var next = await fixture.Evidence(lost.EntityId,persisted,after:Assert.IsType<string>(evidence.NextAfter),limit:1);
        Assert.Equal(firstMark.EntityId,Assert.Single(next.Allocations).QuarantineId);
        Assert.Equal("unknown_stock_loss_recovery",await Reject(() => fixture.Evidence(Guid.NewGuid(),persisted)));
        var stock = await fixture.Quarantines.Snapshot(fixture.Position.EntityId,null,25,token,lossAttribution:true,recoveries:true);
        Assert.Equal((7,1,2,3,0),(stock.OnHandQuantity,stock.QuarantinedQuantity,stock.HeldQuantity,
            stock.OfferBackedQuantity,stock.AvailableToQuarantineQuantity));
        Assert.True(stock.HasOpenShortage);
        var generated = Assert.Single(stock.Quarantines, row=>row.Id==persisted.EntityId);
        Assert.Equal(lost.EntityId,generated.RecoveryPostingId);
        foreach (var previous in stock.Quarantines.Where(row=>row.Id!=generated.Id))
            Assert.Equal((1,0,0),(previous.LostQuantity,previous.ReleasedQuantity,previous.RemainingQuantity));
        Assert.Equal("stock_loss_recovery_client_upgrade_required",await Reject(() =>
            fixture.Quarantines.Evidence(fixture.Position.EntityId,persisted.EntityId,null,token)));
        var qReceipt = await fixture.Quarantines.Evidence(fixture.Position.EntityId,persisted.EntityId,null,token,recoveries:true);
        Assert.Equal(("quarantine",lost.EntityId),(qReceipt.Kind,qReceipt.RecoveryPostingId));

        var eventReader = new StockPositionTimelines(fixture.Db);
        Assert.Equal("stock_loss_recovery_client_upgrade_required",await Reject(() =>
            eventReader.Read(fixture.Position.EntityId,null,25,token,quarantine:true)));
        var timeline = await eventReader.Read(fixture.Position.EntityId,null,25,token,quarantine:true,recoveries:true);
        var movement = Assert.Single(timeline.Events,row=>row.Id==persisted.EntityId);
        Assert.Equal(("stock-loss-recovery",lost.EntityId,2,1,0,0),(movement.Kind,movement.PostingId,
            movement.PhysicalDelta,movement.QuarantineDelta,movement.ReservedDelta,movement.OfferBackedDelta));
        var freshCount = await fixture.Witness(persisted.Revision,7);
        var closure = await new StockShortageClosures(fixture.Db).Save(new StockShortageClosureCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=freshCount.Revision,PurchaseId=fixture.Purchase.EntityId,
            PositionId=fixture.Position.EntityId,LocationId=fixture.Bin.EntityId,ConfirmedLocationCode=fixture.Code,
            CaseId=later.EntityId,ExpectedObservationId=freshCount.EntityId,Reason="Latest fictional count is exact"
        },Guid.NewGuid(),token);
        var release = await fixture.Quarantines.Save(new StockQuarantineReleaseCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=closure.Revision,PurchaseId=fixture.Purchase.EntityId,
            PositionId=fixture.Position.EntityId,LocationId=fixture.Bin.EntityId,ConfirmedLocationCode=fixture.Code,
            CatalogId=fixture.Item.EntityId,CatalogRevision=fixture.Item.Revision,Reason="Reinspected fictional stock",
            Quantity=1,QuarantineId=persisted.EntityId,InspectionResult="passed"
        },Guid.NewGuid(),token);
        var released = await fixture.Quarantines.Evidence(fixture.Position.EntityId,release.EntityId,null,token,recoveries:true);
        Assert.Equal(("quarantine-release","passed",lost.EntityId),(released.Kind,released.InspectionResult,released.RecoveryPostingId));
        var final = await fixture.Lot();
        Assert.Equal((7,1,0,2,3),(final.CurrentOnHandQuantity,final.LostQuantity,final.QuarantinedQuantity,
            final.HeldQuantity,final.OfferBackedQuantity));
        await using var database = await fixture.Db.OpenConnectionAsync(token);
        await using var grants = Database.Query(database,null,"""
            SELECT has_table_privilege(current_user,'brickvault.stock_loss_recoveries','UPDATE'),
                has_column_privilege(current_user,'brickvault.stock_loss_recovery_allocations','basis_slots','INSERT'),
                has_table_privilege(current_user,'brickvault.stock_loss_basis_assignments','INSERT')
            """);
        await using var roles = await grants.ExecuteReaderAsync(token);
        Assert.True(await roles.ReadAsync(token));
        Assert.False(roles.GetBoolean(0));
        Assert.False(roles.GetBoolean(1));
        Assert.False(roles.GetBoolean(2));
    }

    private static async Task<string> Reject(Func<Task> action) => (await Assert.ThrowsAsync<Fault>(action)).Code;

    private sealed class Fixture(NpgsqlDataSource db, Receipt item, Receipt bin, Receipt purchase, Receipt delivery,
        Receipt position, string code, CancellationToken token) : IAsyncDisposable
    {
        public NpgsqlDataSource Db => db;
        public Receipt Item => item;
        public Receipt Bin => bin;
        public Receipt Purchase => purchase;
        public Receipt Delivery => delivery;
        public Receipt Position => position;
        public string Code => code;
        public StockLossRecoveries Recoveries { get; } = new(db);
        public StockQuarantines Quarantines { get; } = new(db);
        public StockReceiptLots Lots { get; } = new(db);

        public static async Task<Fixture> Create(int quantity,int? cost,CancellationToken token)
        {
            var connection=Environment.GetEnvironmentVariable("BV_TEST_DB");
            Assert.SkipWhen(string.IsNullOrEmpty(connection),"BV_TEST_DB with a dedicated bv_m2_test_ database is required.");
            var settings=new NpgsqlConnectionStringBuilder(connection);
            Assert.True(settings.Host is "127.0.0.1" or "::1" && settings.Username=="bv_api" &&
                settings.Database?.StartsWith("bv_m2_test_",StringComparison.Ordinal)==true);
            var db=NpgsqlDataSource.Create(connection!);
            try
            {
                var references=new References(db);
                var receiving=new Receiving(db);
                var tag=Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var code="RECOVERY-"+tag;
                var item=await references.Save(new CatalogCommand
                {
                    CommandId=Guid.NewGuid(),ExpectedRevision=0,ItemType="part",ItemNumber=tag,Variant=null,Name="Fictional returned item",
                    ColorId=0,ColorState="known",Condition="new",Completeness="complete",Source="Fictional catalog",
                    License="operator-owned",Mapping="confirmed"
                },Guid.NewGuid(),token);
                var bin=await references.Save(new LocationCommand
                {
                    CommandId=Guid.NewGuid(),ExpectedRevision=0,Code=code,Name="Fictional bin"
                },Guid.NewGuid(),token);
                var purchase=await receiving.Save(new PurchaseCommand
                {
                    CommandId=Guid.NewGuid(),ExpectedRevision=0,Reference=code,Source="Fictional purchase",
                    Currency="USD",OrderedQuantity=quantity,PendingChargeMinor=null
                },Guid.NewGuid(),token);
                var delivery=await receiving.Save(new DeliveryCommand
                {
                    CommandId=Guid.NewGuid(),ExpectedRevision=purchase.Revision,PurchaseId=purchase.EntityId,
                    Reference="Recovered fixture",Quantity=quantity,CatalogId=item.EntityId,ItemCostMinor=cost
                },Guid.NewGuid(),token);
                var position=await receiving.Save(new PutawayCommand
                {
                    CommandId=Guid.NewGuid(),ExpectedRevision=delivery.Revision,PurchaseId=purchase.EntityId,
                    DeliveryId=delivery.EntityId,CatalogId=item.EntityId,LocationId=bin.EntityId,ConfirmedLocationCode=code,Quantity=quantity
                },Guid.NewGuid(),token);
                return new Fixture(db,item,bin,purchase,delivery,position,code,token);
            }
            catch
            {
                await db.DisposeAsync();
                throw;
            }
        }

        public Task<Receipt> Witness(int revision,int quantity) => new StockCounts(db).Save(new StockCountCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=revision,PurchaseId=purchase.EntityId,PositionId=position.EntityId,
            LocationId=bin.EntityId,ConfirmedLocationCode=code,CountedQuantity=quantity,Reason="Fictional counted inventory"
        },Guid.NewGuid(),token);

        public async Task<Receipt> Lose(int revision,int quantity,StockLossQuarantineSelection[]? attributions=null)
        {
            var witness=await Witness(revision,quantity);
            return await new StockLosses(db).Save(new StockLossCommand
            {
                CommandId=Guid.NewGuid(),ExpectedRevision=witness.Revision,PurchaseId=purchase.EntityId,PositionId=position.EntityId,
                LocationId=bin.EntityId,ConfirmedLocationCode=code,CaseId=witness.EntityId,ExpectedObservationId=witness.EntityId,
                Reason="Fictional shortage",OfferReductions=[],QuarantineAttributions=attributions
            },Guid.NewGuid(),token);
        }

        public StockLossRecoveryCommand Prepare(int revision,Guid posting,params StockLossRecoverySelection[] allocations) => new()
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=revision,PurchaseId=purchase.EntityId,PositionId=position.EntityId,
            LocationId=bin.EntityId,ConfirmedLocationCode=code,PostingId=posting,CatalogId=item.EntityId,
            CatalogRevision=item.Revision,ConfirmedIdentity=true,Reason="Fictional physical identity and inspection confirmed",Allocations=allocations
        };

        public Task<StockLossSnapshot> ReadLoss(Guid loss,bool recoveries=false) =>
            new StockLosses(db).Read(bin.EntityId,position.EntityId,null,null,loss,null,null,25,token,quarantine:true,recoveries:recoveries);
        public Task<StockReceiptLot> Lot() => Lots.Read(delivery.EntityId,null,null,25,25,token,quarantine:true,closures:true,recoveries:true);
        public Task<StockLossRecoveryEvidence> Evidence(Guid loss,Receipt saved,string? after=null,int limit=25) =>
            Recoveries.Evidence(bin.EntityId,position.EntityId,loss,null,saved.CommandId,after,limit,token);

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }
}
