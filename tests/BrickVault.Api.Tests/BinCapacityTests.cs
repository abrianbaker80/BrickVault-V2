
using System.Data;
using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

public sealed partial class StockLossRecoveryTests
{
    private static async Task<PutawayCommand> DraftForBin(Fixture fixture,Guid bin,string label,CancellationToken token)
    {
        var receiving=new Receiving(fixture.Db);
        var order=await receiving.Save(new PurchaseCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=0,Reference="Capacity "+Guid.NewGuid().ToString("N"),
            Source="Fictional capacity test",Currency="USD",OrderedQuantity=1,PendingChargeMinor=null
        },Guid.NewGuid(),token);
        var layer=await receiving.Save(new DeliveryCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=order.Revision,PurchaseId=order.EntityId,
            Reference="One qualifying piece",Quantity=1,CatalogId=fixture.Item.EntityId,ItemCostMinor=17
        },Guid.NewGuid(),token);
        return new PutawayCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=layer.Revision,PurchaseId=order.EntityId,DeliveryId=layer.EntityId,
            CatalogId=fixture.Item.EntityId,LocationId=bin,ConfirmedLocationCode=label,Quantity=1
        };
    }

    private static LocationCapacityCommand Policy(Guid bin,int revision,long? limit) => new()
    {
        CommandId=Guid.NewGuid(),LocationId=bin,ExpectedRevision=revision,PieceLimit=limit,Reason="Fictional measured shelf policy"
    };

    [Fact]
    [Trait("Requires","PostgreSQL")]
    public async Task BinCapacitySerializesIndependentPurchasesAndPreservesExactPolicyReceipts()
    {
        var token=TestContext.Current.CancellationToken;
        await using var fixture=await Fixture.Create(2,55,token);
        var caps=new LocationCapacities(fixture.Db);
        var registry=new LocationRegistry(fixture.Db);
        var receiving=new Receiving(fixture.Db);
        var bin=fixture.Bin.EntityId;
        var initial=await caps.Snapshot(bin,null,25,token);
        Assert.Equal(new LocationCapacityState(null,null,0),initial.Bin.Capacity);
        Assert.Empty(initial.Events);
        Assert.Null((await registry.Lookup(fixture.Code,token)).Bin.Capacity);
        var limit=Policy(bin,fixture.Bin.Revision,3);
        Assert.Equal("invalid_location_capacity",await Reject(()=>caps.Save(limit with {PieceLimit=0},Guid.NewGuid(),token)));
        var set=await caps.Save(limit,Guid.NewGuid(),token);
        Assert.Equal("revision_conflict",await Reject(()=>caps.Save(limit with {CommandId=Guid.NewGuid()},Guid.NewGuid(),token)));
        Assert.Equal(set,await caps.Save(limit,Guid.NewGuid(),token));
        Assert.Equal("command_mismatch",await Reject(()=>caps.Save(limit with {PieceLimit=4},Guid.NewGuid(),token)));

        var one=await DraftForBin(fixture,bin,fixture.Code,token);
        var two=await DraftForBin(fixture,bin,fixture.Code,token);
        async Task<(PutawayCommand Command,Receipt? Receipt,Fault? Fault)> Save(PutawayCommand draft)
        {
            try { return (draft,await receiving.Save(draft,Guid.NewGuid(),token),null); }
            catch (Fault fault) { return (draft,null,fault); }
        }
        var race=await Task.WhenAll(Save(one),Save(two));
        var finish=Assert.Single(race,entry=>entry.Receipt is not null);
        var reject=Assert.Single(race,entry=>entry.Fault is not null);
        Assert.Equal("location_capacity_exceeded",reject.Fault!.Code);
        Assert.Equal(new LocationCapacityRejection(bin,3,3,0,1),reject.Fault.Capacity);
        Assert.Equal(new LocationCapacityState(3,0,0),(await registry.Lookup(fixture.Code,token,true)).Bin.Capacity);
        Assert.Single((await registry.Detail(bin,null,25,null,25,token,true)).Events);
        Assert.Equal(finish.Receipt,await receiving.Save(finish.Command,Guid.NewGuid(),token));

        await using (var connection=await fixture.Db.OpenConnectionAsync(token))
        {
            await using var direct=Database.Query(connection,null,"""
                INSERT INTO brickvault.placements(id,command_id,purchase_id,delivery_id,catalog_id,location_id,location_code,quantity)
                VALUES ($1,$2,$3,$4,$5,$6,$7,1)
                """,Guid.NewGuid(),Guid.NewGuid(),reject.Command.PurchaseId,reject.Command.DeliveryId,reject.Command.CatalogId,bin,fixture.Code);
            Assert.Equal("location_capacity_exceeded",(await Assert.ThrowsAsync<PostgresException>(()=>
                direct.ExecuteNonQueryAsync(token))).ConstraintName);
            await using var role=Database.Query(connection,null,"""
                SELECT has_table_privilege('brickvault.location_capacity_fences','UPDATE')
                    OR has_table_privilege('brickvault.location_capacity_events','UPDATE')
                    OR has_column_privilege('brickvault.location_capacity_events','physical_quantity','INSERT')
                """);
            Assert.Equal(false,await role.ExecuteScalarAsync(token));
        }

        var clear=Policy(bin,set.Revision,null);
        var cleared=await caps.Save(clear,Guid.NewGuid(),token);
        Assert.Equal(cleared,await caps.Save(clear,Guid.NewGuid(),token));
        var acceptedAfterClear=await receiving.Save(reject.Command,Guid.NewGuid(),token);
        Assert.Equal(reject.Command.ExpectedRevision+1,acceptedAfterClear.Revision);
        var after=await caps.Snapshot(bin,null,1,token);
        Assert.Equal(new LocationCapacityState(null,null,0),after.Bin.Capacity);
        Assert.Equal(4,after.Bin.PhysicalQuantity);
        var current=Assert.Single(after.Events);
        Assert.Equal((cleared.EntityId,3L,null,3L,2L),(current.Id,current.PreviousPieceLimit,current.PieceLimit,
            current.PhysicalQuantity,current.PositionCount));
        var history=await caps.Snapshot(bin,after.NextAfter,1,token);
        var original=Assert.Single(history.Events);
        Assert.Equal((set.EntityId,null,3L,2L,1L),(original.Id,original.PreviousPieceLimit,original.PieceLimit,
            original.PhysicalQuantity,original.PositionCount));
        Assert.Equal(original,(await caps.Evidence(bin,null,limit.CommandId,token)).Event);
        Assert.Equal("unknown_location_capacity_event",await Reject(()=>caps.Evidence(Guid.NewGuid(),set.EntityId,null,token)));
        var originalLot=await fixture.Lot();
        Assert.Equal(55,originalLot.RemainingItemCostMinor);
    }

    [Fact]
    [Trait("Requires","PostgreSQL")]
    public async Task BinCapacityRejectsStaleDatabaseSnapshotAndAllowsCrossedTransfers()
    {
        var token=TestContext.Current.CancellationToken;
        await using var a=await Fixture.Create(1,0,token);
        await using var b=await Fixture.Create(1,0,token);
        var caps=new LocationCapacities(a.Db);
        var receiving=new Receiving(a.Db);
        await caps.Save(Policy(a.Bin.EntityId,a.Bin.Revision,3),Guid.NewGuid(),token);
        await caps.Save(Policy(b.Bin.EntityId,b.Bin.Revision,2),Guid.NewGuid(),token);
        var draft=await DraftForBin(a,a.Bin.EntityId,a.Code,token);
        var other=await DraftForBin(a,a.Bin.EntityId,a.Code,token);
        await using (var connection=await a.Db.OpenConnectionAsync(token))
        await using (var isolation=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token))
        {
            await using var inspect=Database.Query(connection,isolation,"""
                SELECT physical_quantity FROM brickvault.location_physical_stock($1)
                """,a.Bin.EntityId);
            Assert.Equal(1L,await inspect.ExecuteScalarAsync(token));
            await receiving.Save(other,Guid.NewGuid(),token);
            await using var insert=Database.Query(connection,isolation,"""
                INSERT INTO brickvault.placements(id,command_id,purchase_id,delivery_id,catalog_id,location_id,location_code,quantity)
                VALUES ($1,$2,$3,$4,$5,$6,$7,1)
                """,Guid.NewGuid(),Guid.NewGuid(),draft.PurchaseId,draft.DeliveryId,draft.CatalogId,draft.LocationId,draft.ConfirmedLocationCode);
            Assert.Equal(PostgresErrorCodes.SerializationFailure,(await Assert.ThrowsAsync<PostgresException>(()=>
                insert.ExecuteNonQueryAsync(token))).SqlState);
        }
        TransferCommand Across(Fixture from,Fixture to) => new()
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=from.Position.Revision,PurchaseId=from.Purchase.EntityId,
            SourcePlacementId=from.Position.EntityId,SourceLocationId=from.Bin.EntityId,ConfirmedSourceLocationCode=from.Code,
            DestinationLocationId=to.Bin.EntityId,ConfirmedDestinationLocationCode=to.Code,
            Quantity=1,Reason="Fictional crossed stock movement",Backing=[]
        };
        var transfers=new Transfers(a.Db);
        var both=await Task.WhenAll(transfers.Save(Across(a,b),Guid.NewGuid(),token),transfers.Save(Across(b,a),Guid.NewGuid(),token));
        Assert.All(both,receipt=>Assert.Equal("transfer",receipt.Kind));
        Assert.Equal(2L,(await caps.Snapshot(a.Bin.EntityId,null,25,token)).Bin.PhysicalQuantity);
        Assert.Equal(1L,(await caps.Snapshot(b.Bin.EntityId,null,25,token)).Bin.PhysicalQuantity);
    }

    [Fact]
    [Trait("Requires","PostgreSQL")]
    public async Task BinCapacityNetsPartOutAndCountsHeldQuarantinedAndRecoveredPieces()
    {
        var token=TestContext.Current.CancellationToken;
        await using var source=await Fixture.Create(4,400,token,partOut:true);
        await using var target=await Fixture.Create(2,37,token,partOut:true);
        var caps=new LocationCapacities(source.Db);
        var receiving=new Receiving(source.Db);
        var parts=new StockPartOuts(source.Db);
        await caps.Save(Policy(source.Bin.EntityId,source.Bin.Revision,2),Guid.NewGuid(),token);
        await caps.Save(Policy(target.Bin.EntityId,target.Bin.Revision,3),Guid.NewGuid(),token);
        var hold=await new BuildHolds(target.Db).Save(new StockHoldCommand
        {
            CommandId=Guid.NewGuid(),PurchaseId=target.Purchase.EntityId,ExpectedRevision=target.Position.Revision,
            PositionId=target.Position.EntityId,LocationId=target.Bin.EntityId,ConfirmedLocationCode=target.Code,Quantity=1,
            Reason="Fictional hold still occupying a shelf",Purpose="Model"
        },Guid.NewGuid(),token);
        await target.Quarantines.Save(new StockQuarantineCommand
        {
            CommandId=Guid.NewGuid(),PurchaseId=target.Purchase.EntityId,ExpectedRevision=hold.Revision,
            PositionId=target.Position.EntityId,LocationId=target.Bin.EntityId,ConfirmedLocationCode=target.Code,
            Quantity=1,Reason="Fictional quarantine still occupying a shelf",CatalogId=target.Item.EntityId,
            CatalogRevision=target.Item.Revision
        },Guid.NewGuid(),token);
        Assert.Equal(new LocationCapacityState(3,1,0),(await caps.Snapshot(target.Bin.EntityId,null,25,token)).Bin.Capacity);
        var command=new StockPartOutCommand
        {
            CommandId=Guid.NewGuid(),PurchaseId=source.Purchase.EntityId,ExpectedRevision=source.Position.Revision,
            SourcePositionId=source.Position.EntityId,SourceLocationId=source.Bin.EntityId,ConfirmedSourceLocationCode=source.Code,
            SourceCatalogId=source.Item.EntityId,SourceCatalogRevision=source.Item.Revision,Quantity=2,
            Reason="Fictional two-piece transformation",ResidualItemCostMinor=100,PhysicalResiduals=[],
            Outputs=[
                new(Guid.NewGuid(),source.Item.EntityId,source.Item.Revision,target.Bin.EntityId,target.Code,1,50,"stock"),
                new(Guid.NewGuid(),source.Item.EntityId,source.Item.Revision,target.Bin.EntityId,target.Code,1,50,"retained")
            ]
        };
        var failed=await Assert.ThrowsAsync<Fault>(()=>parts.Save(command,Guid.NewGuid(),token));
        Assert.Equal("location_capacity_exceeded",failed.Code);
        Assert.Equal(new LocationCapacityRejection(target.Bin.EntityId,3,2,1,2),failed.Capacity);
        Assert.Equal(4L,(await caps.Snapshot(source.Bin.EntityId,null,25,token)).Bin.PhysicalQuantity);
        Assert.Equal(source.Position.Revision,(await parts.Review(source.Position.EntityId,2,null,25,token)).PurchaseRevision);
        var netReduction=command with
        {
            Outputs=[command.Outputs[0] with
            {
                LocationId=source.Bin.EntityId,ConfirmedLocationCode=source.Code,ItemCostMinor=100
            }]
        };
        var approved=await parts.Save(netReduction,Guid.NewGuid(),token);
        Assert.Equal(new LocationCapacityState(2,0,1),(await caps.Snapshot(source.Bin.EntityId,null,25,token)).Bin.Capacity);
        Assert.Equal(approved,await parts.Save(netReduction,Guid.NewGuid(),token));
        var lot=await source.Lots.Read(source.Delivery.EntityId,null,null,25,25,token,partOuts:true);
        Assert.Equal((2,200),(lot.PartOut!.ConsumedQuantity,lot.PartOut.ConsumedItemCostMinor));

        await using var found=await Fixture.Create(2,66,token,partOut:true);
        var loss=await found.Lose(found.Position.Revision,1);
        await caps.Save(Policy(found.Bin.EntityId,found.Bin.Revision,1),Guid.NewGuid(),token);
        var recovery=await found.Recoveries.Save(found.Prepare(loss.Revision,loss.EntityId,new StockLossRecoverySelection(null,0,1)),
            Guid.NewGuid(),token);
        Assert.Equal(33,(await found.Evidence(loss.EntityId,recovery)).RestoredItemCostMinor);
        Assert.Equal(new LocationCapacityState(1,0,1),(await caps.Snapshot(found.Bin.EntityId,null,25,token)).Bin.Capacity);
        var intake=await DraftForBin(found,found.Bin.EntityId,found.Code,token);
        Assert.Equal("location_capacity_exceeded",await Reject(()=>receiving.Save(intake,Guid.NewGuid(),token)));
    }
}
