using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

public sealed class PooledOfferTests
{
    private static Guid Key() => Guid.NewGuid();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static NpgsqlDataSource TestDatabase()
    {
        var cs = Environment.GetEnvironmentVariable("BV_TEST_POOL_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(cs),"BV_TEST_POOL_DB with a dedicated pooled-offer database is required.");
        var options = new NpgsqlConnectionStringBuilder(cs);
        Assert.True(options.Host is "127.0.0.1" or "::1" && options.Username=="bv_api" &&
            options.Database?.StartsWith("bv_m2_test_",StringComparison.Ordinal)==true);
        return NpgsqlDataSource.Create(cs!);
    }
    private sealed record Source(Guid Purchase, Guid Delivery, Guid Position, Guid Bin, string Code, int Revision);
    private static async Task<Receipt> Catalog(NpgsqlDataSource db,string type) =>
        await new References(db).Save(new CatalogCommand
        {
            CommandId=Key(),ExpectedRevision=0,ItemType=type,ItemNumber=Key().ToString("N"),Variant=null,
            Name="Fictional pooled inventory",ColorId=null,ColorState="not-applicable",Condition="used",
            Completeness="complete",Source="Operator observation",License="operator-owned",Mapping="confirmed"
        },Key(),Token);

    private static async Task<Source> Buy(NpgsqlDataSource db,Receipt item,int quantity,string currency,int? cost,SetContents? contents=null)
    {
        var receive=new Receiving(db);
        var code="P-"+Key().ToString("N")[..12].ToUpperInvariant();
        var bin=await new References(db).Save(new LocationCommand
        {CommandId=Key(),ExpectedRevision=0,Code=code,Name="Fictional set inspection bin"},Key(),Token);
        var purchase=await receive.Save(new PurchaseCommand {CommandId=Key(),ExpectedRevision=0,Reference=Key().ToString("N"),
            Source="Fictional supplier receipt",Currency=currency,OrderedQuantity=quantity,PendingChargeMinor=null},Key(),Token);
        var draft=new DeliveryCommand {CommandId=Key(),PurchaseId=purchase.EntityId,ExpectedRevision=1,
            Reference="Receipt layer",Quantity=quantity,ItemCostMinor=cost,CatalogId=item.EntityId};
        var delivery=await receive.Save(contents is null ? draft : draft with {SetContents=contents},Key(),Token);
        var position=await receive.Save(new PutawayCommand {CommandId=Key(),PurchaseId=purchase.EntityId,ExpectedRevision=2,
            DeliveryId=delivery.EntityId,CatalogId=item.EntityId,LocationId=bin.EntityId,ConfirmedLocationCode=code,Quantity=quantity},Key(),Token);
        return new(purchase.EntityId,delivery.EntityId,position.EntityId,bin.EntityId,code,position.Revision);
    }

    private static OfferCommand Single(Source source,Receipt item,int quantity,SetContents? contents=null)
    {
        var command = new OfferCommand {CommandId=Key(),ExpectedRevision=source.Revision,PurchaseId=source.Purchase,CatalogId=item.EntityId,
            UnitPriceMinor=125,Currency="USD",Backing=[new(source.Position,quantity)]};
        return contents is null ? command : command with {SetContents=contents};
    }
    private static PooledOfferCommand Pool(Source a,Source b,Receipt item,int quantity,SetContents? contents=null)
    {
        var command = new PooledOfferCommand {CommandId=Key(),ExpectedRevision=a.Revision,PurchaseId=a.Purchase,CatalogId=item.EntityId,
            ExpectedCatalogRevision=item.Revision,UnitPriceMinor=125,Currency="USD",Backing=[new(a.Position,quantity)],
            AdditionalSources=[new(b.Purchase,b.Revision,[new(b.Position,quantity)])]};
        return contents is null ? command : command with {SetContents=contents};
    }
    private static Task<LossReceivingWorkspace> Workspace(Receiving receiving) => receiving.LossSnapshot(Token,true,true,true,true);
    private static async Task<string> Failure(Func<Task> run) => (await Assert.ThrowsAsync<Fault>(run)).Code;
    private static DraftBackingWithdrawalCommand Withdraw(Source s,Guid offer,int quantity,int previousTotal) => new()
    {CommandId=Key(),PurchaseId=s.Purchase,ExpectedRevision=s.Revision,PositionId=s.Position,LocationId=s.Bin,
        ConfirmedLocationCode=s.Code,OfferId=offer,Quantity=quantity,Reason="Operator selected this source.",ExpectedTotalOfferBackedQuantity=previousTotal};

    [Fact, Trait("Requires","PostgreSQL")]
    public async Task ConcurrentPurchasesKeepOneOfferAndSourceSpecificRevisionsThroughWithdrawalAndLoss()
    {
        await using var db=TestDatabase();
        var receive=new Receiving(db); var item=await Catalog(db,"part");
        var a=await Buy(db,item,1_000_000,"EUR",500); var b=await Buy(db,item,1_000_000,"GBP",null);
        // Make the origin and contributor revisions different without consuming a unit.
        await new StockCounts(db).Save(new StockCountCommand {CommandId=Key(),PurchaseId=a.Purchase,
            ExpectedRevision=a.Revision,PositionId=a.Position,LocationId=a.Bin,ConfirmedLocationCode=a.Code,
            CountedQuantity=1_000_000,Reason="Counted all pieces."},Key(),Token);
        a=a with {Revision=a.Revision+1};
        var draft=Pool(a,b,item,1_000_000);
        Assert.Equal("backing_mismatch",await Failure(()=>receive.Save(draft with
        {Backing=[new(b.Position,1)],AdditionalSources=[new(b.Purchase,b.Revision,[new(a.Position,1)])]},Key(),Token)));
        Assert.Equal("catalog_ineligible",await Failure(()=>receive.Save(draft with {ExpectedCatalogRevision=2},Key(),Token)));
        async Task<(Receipt? Receipt,string? Failure)> Submit(PooledOfferCommand command)
        {
            try { return (await receive.Save(command,Key(),Token),null); }
            catch(Fault fault) { return(null,fault.Code); }
        }
        var rival=Pool(b,a,item,1_000_000);
        var race=await Task.WhenAll(Submit(draft),Submit(rival));
        var receipt=Assert.Single(race,x=>x.Receipt is not null).Receipt!;
        Assert.Equal("revision_conflict",Assert.Single(race,x=>x.Failure is not null).Failure);
        var winner=receipt.CommandId==draft.CommandId?draft:rival;
        Assert.Equal(receipt,await receive.Save(winner,Key(),Token));
        Assert.Equal("command_mismatch",await Failure(()=>receive.Save(winner with {Currency="CAD"},Key(),Token)));
        a=a with {Revision=a.Revision+1};b=b with {Revision=b.Revision+1};
        var data=await Workspace(receive);
        foreach(var src in new[]{a,b})
        {
            var purchase=Assert.Single(data.Purchases,p=>p.Id==src.Purchase);
            Assert.Equal((src.Revision,1_000_000L,1_000_000L,0L,0),(purchase.Revision,purchase.Balances.PhysicalOnHand,
                purchase.Balances.BackedQuantity,purchase.Balances.WithheldQuantity,purchase.Balances.PublishedQuantity));
            Assert.Equal(src==a?"EUR":"GBP",purchase.Currency);
            Assert.Equal(src==a?500L:null,purchase.Balances.KnownItemCostMinor);
            var offer=Assert.Single(purchase.Offers);
            Assert.Equal((receipt.EntityId,1_000_000,2_000_000),(offer.Id,offer.BackedQuantity,offer.Pool!.TotalBackedQuantity));
            Assert.Equal(src.Position,Assert.Single(offer.Backing).PlacementId);
            Assert.Equal(2,offer.Pool.Sources.Count);
            var timeline=await new StockPositionTimelines(db).Read(src.Position,null,20,Token,true,true,true,true,true);
            Assert.Equal(src.Revision,Assert.Single(timeline.Events,e=>e.Kind=="offer-backing").PurchaseRevision);
        }
        Assert.Equal("pooled_offer_client_upgrade_required",await Failure(()=>receive.LossSnapshot(Token,true,true,true)));
        var withdrawals=new DraftBackingWithdrawals(db);
        var fromA=Withdraw(a,receipt.EntityId,2,2_000_000);
        var fromB=Withdraw(b,receipt.EntityId,2,2_000_000);
        Assert.Equal("pooled_offer_client_upgrade_required",await Failure(()=>withdrawals.Save(fromA with {ExpectedTotalOfferBackedQuantity=null},Key(),Token)));
        async Task<(Receipt? Receipt,string? Failure)> Take(DraftBackingWithdrawalCommand command)
        {
            try {return(await withdrawals.Save(command,Key(),Token),null);} catch(Fault error){return(null,error.Code);}
        }
        var attempts=await Task.WhenAll(Take(fromA),Take(fromB));
        var taken=Assert.Single(attempts,x=>x.Receipt is not null).Receipt!;
        Assert.Equal("draft_backing_total_conflict",Assert.Single(attempts,x=>x.Failure is not null).Failure);
        var which=taken.CommandId==fromA.CommandId?a:b;
        var body=taken.CommandId==fromA.CommandId?fromA:fromB;
        Assert.Equal(taken,await withdrawals.Save(body,Key(),Token));
        var evidence=await withdrawals.Evidence(which.Position,taken.EntityId,null,Token,true);
        Assert.Equal((2_000_000,1_999_998),(evidence.PriorTotalOfferBackedQuantity,evidence.RemainingTotalOfferBackedQuantity));
        Assert.Equal("pooled_offer_client_upgrade_required",await Failure(()=>withdrawals.Evidence(which.Position,taken.EntityId,null,Token)));
        var remaining=which==a?b:a;
        var witness=await new StockCounts(db).Save(new StockCountCommand{CommandId=Key(),PurchaseId=remaining.Purchase,
            ExpectedRevision=remaining.Revision,PositionId=remaining.Position,LocationId=remaining.Bin,ConfirmedLocationCode=remaining.Code,
            CountedQuantity=999_999,Reason="Observed one missing unit."},Key(),Token);
        var loss=await new StockLosses(db).Save(new StockLossCommand {CommandId=Key(),PurchaseId=remaining.Purchase,
            ExpectedRevision=witness.Revision,PositionId=remaining.Position,LocationId=remaining.Bin,ConfirmedLocationCode=remaining.Code,
            CaseId=witness.EntityId,ExpectedObservationId=witness.EntityId,Reason="Verified the loss.",
            OfferReductions=[new(receipt.EntityId,1)],QuarantineAttributions=[]},Key(),Token);
        var lossPage=await new StockLosses(db).Read(remaining.Bin,remaining.Position,null,null,loss.EntityId,null,null,25,Token,true);
        Assert.Equal(1_000_000,Assert.Single(lossPage.Reductions).PriorBackedQuantity);
        var after=await Workspace(receive);
        Assert.Equal(1_999_997,Assert.Single(after.Purchases.Single(p=>p.Id==a.Purchase).Offers).Pool!.TotalBackedQuantity);
        var history=await withdrawals.History(remaining.Position,receipt.EntityId,null,25,Token,true,true);
        Assert.Equal(remaining.Revision,Assert.Single(history.Contributions,e=>e.Kind=="offer-backing").Revision);
        Assert.Equal(receipt,await receive.Save(winner,Key(),Token));
    }

    [Fact, Trait("Requires","PostgreSQL")]
    public async Task OfferOriginMustContributePhysicalBacking()
    {
        await using var db=TestDatabase();
        var item=await Catalog(db,"part");
        var origin=await Buy(db,item,1,"USD",1);
        var other=await Buy(db,item,1,"EUR",1);
        await using var connection=await db.OpenConnectionAsync(Token);
        await using var tx=await connection.BeginTransactionAsync(Token);
        var offer=Key();
        await using (var insert=Database.Query(connection,tx,"""
            INSERT INTO brickvault.offers(id,command_id,purchase_id,catalog_id,catalog_revision,
                unit_price_minor,currency,price_scale_verified) VALUES ($1,$2,$3,$4,$5,125,'USD',true)
            """,offer,Key(),origin.Purchase,item.EntityId,item.Revision))
            await insert.ExecuteNonQueryAsync(Token);
        await using (var donor=Database.Query(connection,tx,"""
            INSERT INTO brickvault.offer_backing(purchase_id,offer_id,placement_id,catalog_id,catalog_revision,quantity)
            VALUES ($1,$2,$3,$4,$5,1)
            """,other.Purchase,offer,other.Position,item.EntityId,item.Revision))
            await donor.ExecuteNonQueryAsync(Token);
        await using var check=Database.Query(connection,tx,"SET CONSTRAINTS brickvault.require_offer_backing IMMEDIATE");
        var error=await Assert.ThrowsAsync<PostgresException>(()=>check.ExecuteNonQueryAsync(Token));
        Assert.Equal("receiving_backing",error.ConstraintName);
        await tx.RollbackAsync(Token);
    }

    [Fact, Trait("Requires","PostgreSQL")]
    public async Task EarlierRepeatableReadCannotAttestAnObsoleteWholeOfferTotal()
    {
        await using var db=TestDatabase();
        var receive=new Receiving(db);
        var item=await Catalog(db,"part");
        var origin=await Buy(db,item,2,"USD",1);
        var other=await Buy(db,item,2,"USD",1);
        var offer=await receive.Save(Pool(origin,other,item,2),Key(),Token);
        await using var connection=await db.OpenConnectionAsync(Token);
        await using var earlier=await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,Token);
        await using (var snapshot=Database.Query(connection,earlier,
            "SELECT sum(quantity)::integer FROM brickvault.current_offer_backing WHERE offer_id=$1",offer.EntityId))
            Assert.Equal(4,await snapshot.ExecuteScalarAsync(Token));
        await new DraftBackingWithdrawals(db).Save(Withdraw(origin with {Revision=origin.Revision+1},offer.EntityId,1,4),Key(),Token);
        var competitor=Withdraw(other with {Revision=other.Revision+1},offer.EntityId,1,4);
        // This writer still sees four units. Its immutable withdrawal receipt must never attest that stale total.
        await using var insert=Database.Query(connection,earlier,"""
            INSERT INTO brickvault.draft_backing_withdrawals(id,command_id,purchase_id,position_id,offer_id,location_id,
                location_code,expected_purchase_revision,quantity,reason,expected_offer_total)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
            """,Key(),competitor.CommandId,competitor.PurchaseId,competitor.PositionId,competitor.OfferId,competitor.LocationId,
            competitor.ConfirmedLocationCode,competitor.ExpectedRevision,competitor.Quantity,competitor.Reason,4);
        var error=await Assert.ThrowsAsync<PostgresException>(()=>insert.ExecuteNonQueryAsync(Token));
        Assert.Equal(PostgresErrorCodes.SerializationFailure,error.SqlState);
        await earlier.RollbackAsync(Token);
    }

    [Fact, Trait("Requires","PostgreSQL")]
    public async Task WholeUnclassifiedHeldStockCanBeInspectedAfterLossAndRecoveredToASeparateConfiguration()
    {
        await using var db=TestDatabase();
        var item=await Catalog(db,"set");
        var source=await Buy(db,item,3,"EUR",1200);
        var inspector=new SetPositionInspections(db);
        var bins=new BinStock(db);
        var lots=new StockReceiptLots(db);
        var receive=new Receiving(db);
        var initial=await bins.Snapshot(source.Bin,Token,true);
        Assert.Equal((3L,3L,0L),(initial.PhysicalOnHand,initial.UnbackedQuantity,initial.FreeToBackQuantity));
        Assert.Equal(0,(await lots.Read(source.Delivery,null,null,10,10,Token,true,true,true,true,true)).FreeToBackQuantity);
        Assert.Equal(0,Assert.Single((await Workspace(receive)).Purchases.Single(p=>p.Id==source.Purchase).Deliveries).Placements.Single().FreeToBackQuantity);
        var hold=await new BuildHolds(db).Save(new StockHoldCommand {CommandId=Key(),PurchaseId=source.Purchase,
            ExpectedRevision=source.Revision,PositionId=source.Position,LocationId=source.Bin,ConfirmedLocationCode=source.Code,
            Quantity=2,Purpose="Named build",Reason="Retain both for this project."},Key(),Token);
        var witness=await new StockCounts(db).Save(new StockCountCommand{CommandId=Key(),PurchaseId=source.Purchase,
            ExpectedRevision=hold.Revision,PositionId=source.Position,LocationId=source.Bin,ConfirmedLocationCode=source.Code,
            CountedQuantity=2,Reason="One set missing."},Key(),Token);
        var loss=await new StockLosses(db).Save(new StockLossCommand{CommandId=Key(),PurchaseId=source.Purchase,
            ExpectedRevision=witness.Revision,PositionId=source.Position,LocationId=source.Bin,ConfirmedLocationCode=source.Code,
            CaseId=witness.EntityId,ExpectedObservationId=witness.EntityId,Reason="Post the missing set.",
            OfferReductions=[],QuarantineAttributions=[]},Key(),Token);
        var actual=new SetContents(true,true);
        var choose=new SetInspectionCommand{CommandId=Key(),PurchaseId=source.Purchase,ExpectedRevision=loss.Revision,
            PositionId=source.Position,LocationId=source.Bin,ConfirmedLocationCode=source.Code,CatalogId=item.EntityId,
            CatalogRevision=item.Revision,Quantity=2,SetContents=actual,Reason="Both remaining sets include original box and instructions."};
        var review=await inspector.Review(source.Position,Token);
        Assert.Equal((2,0),(review.PhysicalQuantity,review.FreeToClassifyQuantity));
        Assert.Equal("set_inspection_committed",await Failure(()=>inspector.Save(choose with {Quantity=1},Key(),Token)));
        var inspected=await inspector.Save(choose,Key(),Token);
        var evidence=await inspector.Evidence(source.Position,inspected.EntityId,null,Token);
        Assert.Equal(source.Position,evidence.ResultPositionId);
        Assert.Equal(actual,Assert.Single((await bins.Snapshot(source.Bin,Token,true)).Layers).SetContents);
        var recover=new StockLossRecoveryCommand{CommandId=Key(),PurchaseId=source.Purchase,ExpectedRevision=inspected.Revision,
            PositionId=source.Position,LocationId=source.Bin,ConfirmedLocationCode=source.Code,PostingId=loss.EntityId,
            CatalogId=item.EntityId,CatalogRevision=item.Revision,ConfirmedIdentity=true,Reason="Returned set missing both accessories.",
            Allocations=[new(null,1,0)],SetContents=new SetContents(false,false)};
        var recoveries=new StockLossRecoveries(db);
        var found=await recoveries.Save(recover,Key(),Token);
        var returned=await recoveries.Evidence(source.Bin,source.Position,loss.EntityId,found.EntityId,null,null,10,Token);
        Assert.Equal(400,returned.RestoredItemCostMinor);
        Assert.NotNull(returned.ResultPositionId);
        var final=await bins.Snapshot(source.Bin,Token,true);
        Assert.Equal((3L,1L),(final.PhysicalOnHand,final.FreeToBackQuantity));
        Assert.Equal((2,actual), (final.Layers.Single(p=>p.PlacementId==source.Position).Quantity,
            final.Layers.Single(p=>p.PlacementId==source.Position).SetContents));
        var other=Assert.Single(final.Layers,p=>p.PlacementId==returned.ResultPositionId);
        Assert.Equal((1,recover.SetContents),(other.FreeToBackQuantity,other.SetContents));
        var purchase=(await Workspace(receive)).Purchases.Single(p=>p.Id==source.Purchase);
        Assert.Equal((2L,1200L),(purchase.Balances.Reserved,purchase.Balances.KnownItemCostMinor));
    }

    [Fact, Trait("Requires","PostgreSQL")]
    public async Task PhysicalSetContentsKeepDifferentOffersSeparateAcrossInspectionTransferAndRecovery()
    {
        await using var db=TestDatabase();
        var receive=new Receiving(db);var inspect=new SetPositionInspections(db);var item=await Catalog(db,"set");
        var withBoth=new SetContents(true,true);var without=new SetContents(false,false);
        var a=await Buy(db,item,3,"EUR",1200,withBoth);var b=await Buy(db,item,4,"GBP",0);
        Assert.Equal(withBoth,(await inspect.Review(a.Position,Token)).SetContents);
        Assert.Null((await inspect.Review(b.Position,Token)).SetContents);
        Assert.Equal("set_contents_unverified",await Failure(()=>receive.Save(Single(b,item,1,withBoth),Key(),Token)));
        var split=new SetInspectionCommand{CommandId=Key(),PurchaseId=b.Purchase,ExpectedRevision=b.Revision,PositionId=b.Position,
            LocationId=b.Bin,ConfirmedLocationCode=b.Code,CatalogId=item.EntityId,CatalogRevision=item.Revision,Quantity=2,
            SetContents=withBoth,Reason="Two inspected copies include their box and instructions."};
        var recorded=await inspect.Save(split,Key(),Token);
        Assert.Equal(recorded,await inspect.Save(split,Key(),Token));
        Assert.Equal("command_mismatch",await Failure(()=>inspect.Save(split with {SetContents=without},Key(),Token)));
        var detail=await inspect.Evidence(b.Position,null,split.CommandId,Token);
        Assert.NotEqual(b.Position,detail.ResultPositionId);
        Assert.Equal(detail.Id,(await inspect.Evidence(detail.ResultPositionId,detail.Id,null,Token)).Id);
        Assert.Equal(withBoth,(await inspect.Review(detail.ResultPositionId,Token)).SetContents);
        Assert.Equal(2,(await inspect.Review(b.Position,Token)).PhysicalQuantity);
        var whole=await inspect.Save(split with {CommandId=Key(),ExpectedRevision=recorded.Revision,SetContents=without},Key(),Token);
        var right=b with {Revision=whole.Revision,Position=detail.ResultPositionId};
        var wrong=b with {Revision=whole.Revision};
        Assert.Equal("set_contents_mismatch",await Failure(()=>receive.Save(Pool(a,wrong,item,1,withBoth),Key(),Token)));
        Assert.Equal("set_contents_mismatch",await Failure(()=>receive.Save(Single(right,item,1,withBoth) with
            {Backing=[new(right.Position,1),new(wrong.Position,1)]},Key(),Token)));
        var pooled=await receive.Save(Pool(a,right,item,1,withBoth),Key(),Token);
        var bRevision=whole.Revision+1;
        var transfer=new TransferCommand{CommandId=Key(),ExpectedRevision=bRevision,PurchaseId=b.Purchase,
            SourcePlacementId=right.Position,SourceLocationId=b.Bin,ConfirmedSourceLocationCode=b.Code,DestinationLocationId=a.Bin,
            ConfirmedDestinationLocationCode=a.Code,Quantity=1,Reason="Move the boxed set to its picked shelf.",Backing=[new(pooled.EntityId,1)]};
        var moved=await new Transfers(db).Save(transfer,Key(),Token);
        Assert.Equal(withBoth,(await inspect.Review(moved.EntityId,Token)).SetContents);
        var legacy=await receive.Save(Single(wrong with {Revision=moved.Revision},item,1,without),Key(),Token);
        var data=await Workspace(receive);var stock=data.Purchases.Single(p=>p.Id==b.Purchase);
        Assert.Equal((4L,0L,2L),(stock.Balances.PhysicalOnHand,stock.Balances.KnownItemCostMinor,stock.Balances.BackedQuantity));
        Assert.Equal(without,stock.Offers.Single(o=>o.Id==legacy.EntityId).SetContents);
        Assert.Equal(withBoth,stock.Offers.Single(o=>o.Id==pooled.EntityId).SetContents);
        // A found unit can have different contents from its original position. Restore its cost
        // from the original loss, then record the observed accessories at a separate position.
        var count=await new StockCounts(db).Save(new StockCountCommand{CommandId=Key(),PurchaseId=a.Purchase,
            ExpectedRevision=a.Revision+1,PositionId=a.Position,LocationId=a.Bin,ConfirmedLocationCode=a.Code,CountedQuantity=2,
            Reason="Observed a missing set."},Key(),Token);
        var posted=await new StockLosses(db).Save(new StockLossCommand{CommandId=Key(),PurchaseId=a.Purchase,
            ExpectedRevision=count.Revision,PositionId=a.Position,LocationId=a.Bin,ConfirmedLocationCode=a.Code,CaseId=count.EntityId,
            ExpectedObservationId=count.EntityId,Reason="Record missing set.",OfferReductions=[],QuarantineAttributions=[]},Key(),Token);
        var recover=new StockLossRecoveryCommand{CommandId=Key(),PurchaseId=a.Purchase,ExpectedRevision=posted.Revision,
            PositionId=a.Position,LocationId=a.Bin,ConfirmedLocationCode=a.Code,PostingId=posted.EntityId,
            CatalogId=item.EntityId,CatalogRevision=item.Revision,ConfirmedIdentity=true,Reason="Found set without box or manual.",
            Allocations=[new(null,0,1)],SetContents=without};
        var recovered=await new StockLossRecoveries(db).Save(recover,Key(),Token);
        var proof=await new StockLossRecoveries(db).Evidence(a.Bin,a.Position,posted.EntityId,recovered.EntityId,null,null,20,Token);
        Assert.Equal(without,proof.SetContents);Assert.NotNull(proof.ResultPositionId);
        Assert.Equal(400,proof.RestoredItemCostMinor);
        Assert.Equal(1,(await inspect.Review(proof.ResultPositionId.Value,Token)).PhysicalQuantity);
        Assert.Equal(2,(await inspect.Review(a.Position,Token)).PhysicalQuantity);
        Assert.Equal(without,(await inspect.Review(proof.ResultPositionId.Value,Token)).SetContents);
        var after=(await Workspace(receive)).Purchases.Single(p=>p.Id==a.Purchase);
        Assert.Equal((3L,1L,1200L),(after.Balances.PhysicalOnHand,after.Balances.Unavailable,after.Balances.KnownItemCostMinor));
        Assert.Equal(recovered,await new StockLossRecoveries(db).Save(recover,Key(),Token));
        var withdrawal=new DraftBackingWithdrawals(db);
        var candidates=await withdrawal.Snapshot(a.Position,null,10,Token,true,true);
        Assert.Equal(withBoth,Assert.Single(candidates.Offers).SetContents);
        var selected=await withdrawal.History(a.Position,pooled.EntityId,null,10,Token,true,true);
        Assert.Equal(withBoth,selected.Offer.SetContents);
        var withdrawn=await withdrawal.Save(Withdraw(a with {Revision=recovered.Revision},pooled.EntityId,1,2),Key(),Token);
        Assert.Equal(withBoth,(await withdrawal.Evidence(a.Position,withdrawn.EntityId,null,Token,true)).SetContents);
    }
}
