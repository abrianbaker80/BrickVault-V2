
using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;
using Xunit;

namespace BrickVault.Api.Tests;

public sealed partial class StockLossRecoveryTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task BinRegistryRetainsUniqueLabelsAndBlocksOnlyNewIncomingStock()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await Fixture.Create(3,300,ct);
        var registry=new LocationRegistry(fixture.Db);
        var references=new References(fixture.Db);
        var receiving=new Receiving(fixture.Db);
        var transfers=new Transfers(fixture.Db);
        var tag=Guid.NewGuid().ToString("N")[..10];
        var otherCode="BIN-"+tag.ToUpperInvariant();
        var alternate="Shelf-"+tag;
        var other=await references.Save(new LocationCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=0,Code=otherCode,Name="Fictional receiving bin"
        },Guid.NewGuid(),ct);
        var alias=new LocationLabelCommand
        {
            CommandId=Guid.NewGuid(),LocationId=fixture.Bin.EntityId,ExpectedRevision=fixture.Bin.Revision,Label=alternate
        };
        var posted=await Task.WhenAll(registry.Save(alias,Guid.NewGuid(),ct),registry.Save(alias,Guid.NewGuid(),ct));
        var labelReceipt=posted[0];
        Assert.Equal(labelReceipt,posted[1]);
        var aliasProof=await registry.Evidence(fixture.Bin.EntityId,null,alias.CommandId,ct);
        Assert.Equal((labelReceipt.EntityId,alias.Label,labelReceipt.Revision,"label-added"),
            (aliasProof.Event.Id,aliasProof.Event.Label,aliasProof.Event.Revision,aliasProof.Event.Kind));
        Assert.Equal("unknown_location_registry_event",await Reject(() => registry.Evidence(other.EntityId,labelReceipt.EntityId,null,ct)));
        Assert.Equal("duplicate_location_label",await Reject(() => registry.Save(alias with
        {
            CommandId=Guid.NewGuid(),LocationId=other.EntityId,ExpectedRevision=other.Revision,Label=alternate.ToUpperInvariant()
        },Guid.NewGuid(),ct)));
        Assert.Equal("duplicate_location_label",await Reject(() => references.Save(new LocationCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=other.Revision,Id=other.EntityId,Code=alternate,Name="Conflicting canonical label"
        },Guid.NewGuid(),ct)));
        var rename=new LocationCommand
        {
            CommandId=Guid.NewGuid(),Id=fixture.Bin.EntityId,ExpectedRevision=labelReceipt.Revision,
            Code="RENAMED-"+tag.ToUpperInvariant(),Name="Fictional renamed bin"
        };
        var renamed=await references.Save(rename,Guid.NewGuid(),ct);
        var named=await registry.Lookup(rename.Code.ToLowerInvariant(),ct);
        var original=await registry.Lookup(fixture.Code,ct);
        var scanned=await registry.Lookup(alternate.ToLowerInvariant(),ct);
        Assert.Equal((fixture.Bin.EntityId,true,rename.Code),(named.Bin.Location.Id,named.IsCanonical,named.MatchedLabel));
        Assert.Equal((fixture.Bin.EntityId,false,fixture.Code),(original.Bin.Location.Id,original.IsCanonical,original.MatchedLabel));
        Assert.Equal((fixture.Bin.EntityId,false,alternate),(scanned.Bin.Location.Id,scanned.IsCanonical,scanned.MatchedLabel));
        Assert.Equal("unknown_location_label",await Reject(() => registry.Lookup("MISSING-"+tag,ct)));

        var inbound=new TransferCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=fixture.Position.Revision,PurchaseId=fixture.Purchase.EntityId,
            SourcePlacementId=fixture.Position.EntityId,SourceLocationId=fixture.Bin.EntityId,ConfirmedSourceLocationCode=alternate.ToUpperInvariant(),
            DestinationLocationId=other.EntityId,ConfirmedDestinationLocationCode=otherCode,Quantity=1,Reason="Fictional reserve movement",Backing=[]
        };
        var outward=await transfers.Save(inbound,Guid.NewGuid(),ct);
        var moveBack=inbound with
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=outward.Revision,SourcePlacementId=outward.EntityId,SourceLocationId=other.EntityId,
            ConfirmedSourceLocationCode=otherCode,DestinationLocationId=fixture.Bin.EntityId,ConfirmedDestinationLocationCode=alternate.ToLowerInvariant()
        };
        var acceptedBefore=await transfers.Save(moveBack,Guid.NewGuid(),ct);
        var archive=new LocationArchiveCommand
        {
            CommandId=Guid.NewGuid(),LocationId=fixture.Bin.EntityId,ExpectedRevision=renamed.Revision,ConfirmedLabel=alternate.ToUpperInvariant(),
            Reason="Fictional physical shelf is retiring",ExpectedPhysicalQuantity=3,ExpectedPositionCount=2
        };
        Assert.Equal("location_archive_stock_changed",await Reject(() => registry.Archive(
            archive with {ExpectedPhysicalQuantity=2},Guid.NewGuid(),ct)));
        Assert.Equal("location_label_mismatch",await Reject(() => registry.Archive(
            archive with {ConfirmedLabel=otherCode},Guid.NewGuid(),ct)));
        var archived=await registry.Archive(archive,Guid.NewGuid(),ct);
        Assert.Equal(archived,await registry.Archive(archive,Guid.NewGuid(),ct));
        Assert.Equal(acceptedBefore,await transfers.Save(moveBack,Guid.NewGuid(),ct));
        var decided=await registry.Evidence(fixture.Bin.EntityId,archived.EntityId,null,ct);
        Assert.Equal(("archived",alternate,3L,2L,archived.Revision),
            (decided.Event.Kind,decided.Event.Label,decided.Event.PhysicalQuantity,decided.Event.PositionCount,decided.Event.Revision));
        var current=await registry.Detail(fixture.Bin.EntityId,null,1,null,1,ct);
        Assert.True(current.Bin.Archived);
        Assert.Equal(archived.EntityId,current.Bin.ArchiveEventId);
        Assert.NotNull(current.NextLabelAfter);
        Assert.Equal(archived.EntityId,Assert.Single(current.Events).Id);
        var remaining=await registry.Detail(fixture.Bin.EntityId,current.NextLabelAfter,25,current.NextEventAfter,25,ct);
        Assert.Equal(2,remaining.Labels.Count);
        Assert.Equal(new[]{"canonical-renamed","label-added","registered"},remaining.Events.Select(r=>r.Kind));
        Assert.Equal("invalid_location_registry_cursor",await Reject(() =>
            registry.Detail(other.EntityId,current.NextLabelAfter,25,current.NextEventAfter,25,ct)));

        var counted=await new StockCounts(fixture.Db).Save(new StockCountCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=acceptedBefore.Revision,PurchaseId=fixture.Purchase.EntityId,
            PositionId=fixture.Position.EntityId,LocationId=fixture.Bin.EntityId,ConfirmedLocationCode=fixture.Code.ToLowerInvariant(),
            CountedQuantity=2,Reason="Count existing stock in the archived bin"
        },Guid.NewGuid(),ct);
        var outAfter=inbound with {CommandId=Guid.NewGuid(),ExpectedRevision=counted.Revision};
        var movedOut=await transfers.Save(outAfter,Guid.NewGuid(),ct);
        var newIncoming=moveBack with {CommandId=Guid.NewGuid(),ExpectedRevision=movedOut.Revision,SourcePlacementId=movedOut.EntityId};
        Assert.Equal("location_destination_archived",await Reject(() => transfers.Save(newIncoming,Guid.NewGuid(),ct)));
        await using (var connection=await fixture.Db.OpenConnectionAsync(ct))
        {
            await using var direct=Database.Query(connection,null,"""
                INSERT INTO brickvault.placement_transfers(id,command_id,purchase_id,source_position_id,
                    source_location_id,source_location_code,destination_location_id,destination_location_code,quantity,reason)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,1,'Direct fictional transfer')
                """,Guid.NewGuid(),Guid.NewGuid(),fixture.Purchase.EntityId,movedOut.EntityId,other.EntityId,otherCode,
                    fixture.Bin.EntityId,alternate);
            var rejected=await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteNonQueryAsync(ct));
            Assert.Equal("location_destination_archived",rejected.ConstraintName);
            await using var role=Database.Query(connection,null,"""
                SELECT has_table_privilege('brickvault.location_labels','INSERT')
                    OR has_column_privilege('brickvault.location_registry_events','canonical_code','INSERT')
                    OR has_table_privilege('brickvault.location_registry_events','UPDATE')
                """);
            Assert.Equal(false,await role.ExecuteScalarAsync(ct));
        }

        var blockedPartOut=new StockPartOutCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=movedOut.Revision,PurchaseId=fixture.Purchase.EntityId,
            SourcePositionId=movedOut.EntityId,SourceLocationId=other.EntityId,ConfirmedSourceLocationCode=otherCode,
            SourceCatalogId=fixture.Item.EntityId,SourceCatalogRevision=fixture.Item.Revision,
            Quantity=1,Reason="Fictional transformation",ResidualItemCostMinor=100,PhysicalResiduals=[],
            Outputs=[new(Guid.NewGuid(),fixture.Item.EntityId,fixture.Item.Revision,fixture.Bin.EntityId,alternate,1,0,"stock")]
        };
        Assert.Equal("location_destination_archived",await Reject(() => new StockPartOuts(fixture.Db).Save(blockedPartOut,Guid.NewGuid(),ct)));
        var purchase=await receiving.Save(new PurchaseCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=0,Reference="Other "+tag,Source="Fictional new purchase",Currency="USD",
            OrderedQuantity=1,PendingChargeMinor=null
        },Guid.NewGuid(),ct);
        var delivery=await receiving.Save(new DeliveryCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=purchase.Revision,PurchaseId=purchase.EntityId,Reference="One new intake",
            Quantity=1,CatalogId=fixture.Item.EntityId,ItemCostMinor=0
        },Guid.NewGuid(),ct);
        var putaway=new PutawayCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=delivery.Revision,PurchaseId=purchase.EntityId,DeliveryId=delivery.EntityId,
            CatalogId=fixture.Item.EntityId,LocationId=fixture.Bin.EntityId,ConfirmedLocationCode=alternate,Quantity=1
        };
        Assert.Equal("location_destination_archived",await Reject(() => receiving.Save(putaway,Guid.NewGuid(),ct)));
        Assert.Equal(delivery.Revision+1,(await receiving.Save(putaway with
        {
            LocationId=other.EntityId,ConfirmedLocationCode=otherCode
        },Guid.NewGuid(),ct)).Revision);
        Assert.Equal(movedOut.Revision,(await new StockPartOuts(fixture.Db).Review(movedOut.EntityId,1,null,25,ct)).PurchaseRevision);
        var after=await registry.Lookup(alternate,ct);
        Assert.True(after.Bin.Archived);
        Assert.Equal((2L,2L),(after.Bin.PhysicalQuantity,after.Bin.PositionCount));
        Assert.Equal((3L,2L),((await registry.Evidence(fixture.Bin.EntityId,archived.EntityId,null,ct)).Event.PhysicalQuantity,
            decided.Event.PositionCount));
        var timeline=await new StockPositionTimelines(fixture.Db).Read(fixture.Position.EntityId,null,25,ct);
        Assert.Contains(timeline.Events,e=>e.Id==movedOut.EntityId && e.LocationCodeAtEvent==alternate);
        Assert.Contains(timeline.Events,e=>e.Id==counted.EntityId && e.LocationCodeAtEvent==fixture.Code);
        Assert.Contains((await references.Workspace(ct)).Locations,location=>location.Id==fixture.Bin.EntityId && location.Code==rename.Code);
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task BinRegistryArchiveWaitsForInFlightStockAndRejectsStaleWitness()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await Fixture.Create(2,0,ct);
        var registry=new LocationRegistry(fixture.Db);
        var code="RACE-"+Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var target=await new References(fixture.Db).Save(new LocationCommand
        {
            ExpectedRevision=0,CommandId=Guid.NewGuid(),Code=code,Name="Fictional archive race bin"
        },Guid.NewGuid(),ct);
        var draft=new LocationArchiveCommand
        {
            CommandId=Guid.NewGuid(),LocationId=target.EntityId,ExpectedRevision=target.Revision,ConfirmedLabel=code,
            Reason="Confirm actual stock after concurrent transfer",ExpectedPhysicalQuantity=0,ExpectedPositionCount=0
        };
        await using var connection=await fixture.Db.OpenConnectionAsync(ct);

        async Task ObserveWait(Task operation)
        {
            await using var observer=await fixture.Db.OpenConnectionAsync(ct);
            var timer=Stopwatch.StartNew();
            while (!operation.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                await using var query=Database.Query(observer,null,"""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity a
                        WHERE a.datname=current_database() AND $1=ANY(pg_blocking_pids(a.pid)))
                    """,connection.ProcessID);
                if (await query.ExecuteScalarAsync(ct) is true) return;
                await Task.Delay(TimeSpan.FromMilliseconds(20),ct);
            }
            Assert.Fail("Expected an observed PostgreSQL lock wait on the earlier transaction.");
        }

        await using var tx=await connection.BeginTransactionAsync(ct);
        var move=new Receipt(Guid.NewGuid(),Guid.NewGuid(),fixture.Position.Revision+1,"transfer",Guid.NewGuid());
        await using (var advance=Database.Query(connection,tx,"""
            UPDATE brickvault.purchases SET revision=revision+1 WHERE id=$1 AND revision=$2
            """,fixture.Purchase.EntityId,fixture.Position.Revision))
            Assert.Equal(1,await advance.ExecuteNonQueryAsync(ct));
        await using (var movement=Database.Query(connection,tx,"""
            INSERT INTO brickvault.placement_transfers(id,command_id,purchase_id,source_position_id,
                source_location_id,source_location_code,destination_location_id,destination_location_code,quantity,reason)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,1,'Fictional in-flight stock movement')
            """,move.EntityId,move.CommandId,fixture.Purchase.EntityId,fixture.Position.EntityId,fixture.Bin.EntityId,fixture.Code,
                target.EntityId,code))
            Assert.Equal(1,await movement.ExecuteNonQueryAsync(ct));
        await CommandJournal.Append(connection,tx,move,SHA256.HashData("Fictional concurrent movement"u8),"record-receiving",ct);

        var archive=registry.Archive(draft,Guid.NewGuid(),ct);
        await ObserveWait(archive);
        await tx.CommitAsync(ct);
        Assert.Equal("location_archive_stock_changed",await Reject(()=>archive));
        Assert.False((await registry.Lookup(code,ct)).Bin.Archived);

        await using var archiveTx=await connection.BeginTransactionAsync(ct);
        var receipt=new Receipt(draft.CommandId,Guid.NewGuid(),target.Revision+1,"location-archive",Guid.NewGuid());
        await using (var capture=Database.Query(connection,archiveTx,"""
            INSERT INTO brickvault.location_registry_events(
                id,command_id,location_id,revision,kind,label,reason,physical_quantity,position_count)
            VALUES ($1,$2,$3,$4,'archived',$5,'Fictional direct receipt with matched stock',1,1)
            """,receipt.EntityId,receipt.CommandId,target.EntityId,receipt.Revision,code))
            Assert.Equal(1,await capture.ExecuteNonQueryAsync(ct));
        await CommandJournal.Append(connection,archiveTx,receipt,SHA256.HashData("Fictional archive"u8),"record-reference",ct);
        var incoming=new Transfers(fixture.Db).Save(new TransferCommand
        {
            CommandId=Guid.NewGuid(),PurchaseId=fixture.Purchase.EntityId,ExpectedRevision=move.Revision,
            SourcePlacementId=fixture.Position.EntityId,SourceLocationId=fixture.Bin.EntityId,ConfirmedSourceLocationCode=fixture.Code,
            DestinationLocationId=target.EntityId,ConfirmedDestinationLocationCode=code,Quantity=1,Backing=[],Reason="Fictional racing transfer"
        },Guid.NewGuid(),ct);
        await ObserveWait(incoming);
        await archiveTx.CommitAsync(ct);
        Assert.Equal("location_destination_archived",await Reject(()=>incoming));
        var detail=await registry.Detail(target.EntityId,null,25,null,25,ct);
        Assert.Equal((true,1L,1L,receipt.EntityId),(detail.Bin.Archived,detail.Bin.PhysicalQuantity,detail.Bin.PositionCount,detail.Bin.ArchiveEventId));
    }
}
