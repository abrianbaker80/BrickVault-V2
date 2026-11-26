using Xunit;

namespace BrickVault.Api.Tests;

public sealed partial class StockLossRecoveryTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    [InlineData(null)]
    [Trait("Requires", "PostgreSQL")]
    public async Task ManualPartOutConservesBasisAcrossLossRecoveryAndLaterLoss(int? inputBasis)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(3, inputBasis, token);
        var first = await fixture.Lose(fixture.Position.Revision, 2);
        var second = await fixture.Lose(first.Revision, 1);
        var restored = await fixture.Recoveries.Save(fixture.Prepare(second.Revision,first.EntityId,new StockLossRecoverySelection(null,1,0)),
            Guid.NewGuid(),token);
        var parts = new StockPartOuts(fixture.Db);
        var quoted = await parts.Review(fixture.Position.EntityId,1,null,1,token);
        Assert.Equal((2,0,restored.Revision,true,inputBasis/3),(quoted.AvailableQuantity,quoted.HeldQuantity,
            quoted.PurchaseRevision,quoted.SelectionAvailable,quoted.SourceItemCostMinor));
        var references = new References(fixture.Db);
        async Task<Receipt> Catalog(string title) => await references.Save(new CatalogCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=0,ItemType="part",ItemNumber=Guid.NewGuid().ToString("N")[..12],Name=title,Variant=null,
            ColorId=0,ColorState="known",Condition="new",Completeness="complete",Source="Fictional part-out catalog",
            License="operator-owned",Mapping="confirmed"
        },Guid.NewGuid(),token);
        var child = await Catalog("Fictional brick");
        var retained = await Catalog("Fictional plate");
        var destination = await references.Save(new LocationCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=0,Code="PART-"+Guid.NewGuid().ToString("N")[..12],Name="Fictional destination"
        },Guid.NewGuid(),token);
        var command = new StockPartOutCommand
        {
            CommandId=Guid.NewGuid(),PurchaseId=fixture.Purchase.EntityId,ExpectedRevision=restored.Revision,
            SourcePositionId=fixture.Position.EntityId,SourceLocationId=fixture.Bin.EntityId,ConfirmedSourceLocationCode=fixture.Code,
            SourceCatalogId=fixture.Item.EntityId,SourceCatalogRevision=fixture.Item.Revision,Quantity=1,Reason="Fictional manual separation",
            Outputs=[
                new(Guid.NewGuid(),child.EntityId,child.Revision,fixture.Bin.EntityId,fixture.Code,2,inputBasis is 100?18:inputBasis,"stock"),
                new(Guid.NewGuid(),retained.EntityId,retained.Revision,fixture.Bin.EntityId,fixture.Code,1,inputBasis is 100?11:inputBasis,"retained"),
                new(Guid.NewGuid(),retained.EntityId,retained.Revision,fixture.Bin.EntityId,fixture.Code,1,null,"retained")
            ],
            ResidualItemCostMinor=inputBasis is 100?4:inputBasis,
            PhysicalResiduals=[new(Guid.NewGuid(),"discarded","Unusable fictional fragments",2),new(Guid.NewGuid(),"lost","One documented fragment",1)]
        };
        Task<Receipt> Save(StockPartOutCommand c) => parts.Save(c,Guid.NewGuid(),token);
        Assert.Equal("part_out_bin",await Reject(()=>Save(command with {ConfirmedSourceLocationCode="WRONG"})));
        Assert.Equal("part_out_catalog",await Reject(()=>Save(command with {SourceCatalogRevision=fixture.Item.Revision+1})));
        Assert.Equal("part_out_cost_mismatch",await Reject(()=>Save(command with {ResidualItemCostMinor=1})));
        var attempts = await Task.WhenAll(Save(command),Save(command with {Outputs=[..command.Outputs.Reverse()]}));
        var saved=attempts[0];
        Assert.Equal(saved,attempts[1]);
        Assert.Equal(("part-out",restored.Revision+1),(saved.Kind,saved.Revision));
        Assert.Equal("revision_conflict",await Reject(()=>Save(command with {CommandId=Guid.NewGuid()})));
        var proof = await parts.Evidence(fixture.Position.EntityId,null,command.CommandId,null,1,null,1,token);
        Assert.Equal((saved.EntityId,saved.CorrelationId,inputBasis/3,command.ResidualItemCostMinor),
            (proof.Id,proof.CorrelationId,proof.SourceItemCostMinor,proof.ResidualItemCostMinor));
        var secondPage = await parts.Evidence(fixture.Position.EntityId,saved.EntityId,null,proof.NextOutputAfter,25,proof.NextResidualAfter,25,token);
        Assert.Equal(2,secondPage.Outputs.Count);
        Assert.Single(secondPage.PhysicalResiduals);
        Assert.Null(secondPage.NextOutputAfter);
        var all = await parts.Evidence(fixture.Position.EntityId,saved.EntityId,null,null,25,null,25,token);
        var available = Assert.Single(all.Outputs, item=>item.Disposition=="stock");
        var isolated = Assert.Single(all.Outputs, item=>item.Disposition=="retained" && item.LineId==command.Outputs[1].LineId);
        Assert.Equal((2,command.Outputs[0].ItemCostMinor,child.EntityId,null),
            (available.Quantity,available.ItemCostMinor,available.RecordedCatalog.Id,available.QuarantineId));
        Assert.Equal(isolated.PositionId,isolated.QuarantineId);
        Assert.Equal("unknown_stock_part_out",await Reject(()=>parts.Evidence(available.PositionId,saved.EntityId,null,null,25,null,25,token)));
        Assert.Equal("invalid_stock_part_out_cursor",await Reject(()=>parts.Evidence(fixture.Position.EntityId,saved.EntityId,null,
            $"out.{Guid.NewGuid():N}.{available.LineId:N}",25,null,25,token)));
        var q = await fixture.Quarantines.Evidence(isolated.PositionId,isolated.QuarantineId,null,token,partOuts:true);
        Assert.Equal(("quarantine",saved.EntityId,fixture.Position.EntityId),(q.Kind,q.PartOutId,q.PartOutSourcePositionId));
        Assert.Equal("stock_part_out_client_upgrade_required",await Reject(()=>fixture.Quarantines.Evidence(isolated.PositionId,isolated.QuarantineId,null,token)));
        var release = await fixture.Quarantines.Save(new StockQuarantineReleaseCommand
        {
            CommandId=Guid.NewGuid(),PurchaseId=fixture.Purchase.EntityId,ExpectedRevision=saved.Revision,
            PositionId=isolated.PositionId,LocationId=fixture.Bin.EntityId,ConfirmedLocationCode=fixture.Code,
            CatalogId=retained.EntityId,CatalogRevision=retained.Revision,Quantity=1,QuarantineId=isolated.QuarantineId!.Value,
            InspectionResult="passed",Reason="Identified retained material passed later inspection"
        },Guid.NewGuid(),token);
        Assert.Equal(saved.EntityId,(await fixture.Quarantines.Evidence(isolated.PositionId,release.EntityId,null,token,partOuts:true)).PartOutId);
        var sourceLot=await fixture.Lots.Read(fixture.Delivery.EntityId,null,null,25,25,token,quarantine:true,recoveries:true,partOuts:true);
        Assert.Equal((3,0,1,1,1),(sourceLot.ReceivedQuantity,sourceLot.UnplacedQuantity,sourceLot.CurrentOnHandQuantity,
            sourceLot.LostQuantity,sourceLot.PartOut!.ConsumedQuantity));
        Assert.Equal(inputBasis is 100?34:inputBasis,sourceLot.RemainingItemCostMinor);
        var childLot = await fixture.Lots.Read(available.DeliveryId,null,null,25,25,token,quarantine:true,partOuts:true);
        Assert.Equal((saved.EntityId,fixture.Delivery.EntityId,fixture.Position.EntityId),
            (childLot.PartOut!.ParentTransformationId,childLot.PartOut.SourceDeliveryId,childLot.PartOut.SourcePositionId));
        Assert.Equal(available.ItemCostMinor,childLot.RemainingItemCostMinor);
        var timeline = await new StockPositionTimelines(fixture.Db).Read(available.PositionId,null,25,token,quarantine:true,partOuts:true);
        var introduced = Assert.Single(timeline.Events);
        Assert.Equal(("part-out-output",2,saved.EntityId),(introduced.Kind,introduced.PhysicalDelta,introduced.TransformationId));
        var loss = await fixture.Lose(release.Revision,0);
        var history = await new StockLosses(fixture.Db).Read(fixture.Bin.EntityId,fixture.Position.EntityId,null,null,loss.EntityId,null,null,25,
            token,quarantine:true,recoveries:true,partOuts:true);
        Assert.Equal(inputBasis is 100?34:inputBasis,history.AllocatedItemCostMinor);
        Assert.Equal((1,inputBasis/3,inputBasis is null?null:0),
            (history.PriorTransformedQuantity,history.PriorTransformedItemCostMinor,history.RemainingDeliveryItemCostMinor));

        var chained = command with
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=loss.Revision,SourcePositionId=available.PositionId,
            SourceCatalogId=child.EntityId,SourceCatalogRevision=child.Revision,ResidualItemCostMinor=available.ItemCostMinor/2,
            Outputs=[new(Guid.NewGuid(),retained.EntityId,retained.Revision,destination.EntityId,
                "WRONG",1,null,"stock")],PhysicalResiduals=[]
        };
        Assert.Equal("part_out_bin",await Reject(()=>Save(chained)));
        var target = await references.Workspace(token);
        var registeredCode = Assert.Single(target.Locations,row=>row.Id==destination.EntityId).Code;
        var written = await Save(chained with {Outputs=[chained.Outputs[0] with {ConfirmedLocationCode=registeredCode}]});
        var lineage = await parts.Evidence(available.PositionId,written.EntityId,null,null,25,null,25,token);
        Assert.Equal(available.DeliveryId,lineage.Receipt.DeliveryId);
        Assert.Null(Assert.Single(lineage.Outputs).ItemCostMinor);
        var descendant = Assert.Single(lineage.Outputs);
        var diagnostic = await references.Diagnostic(descendant.CorrelationId,token);
        Assert.Equal(("part-out-output",descendant.CommandId,descendant.DeliveryId,written.CommandId,written.CorrelationId),
            (diagnostic.Kind,diagnostic.CommandId,diagnostic.EntityId,diagnostic.ParentCommandId,diagnostic.ParentCorrelationId));

        var hold = await new BuildHolds(fixture.Db).Save(new StockHoldCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=written.Revision,PurchaseId=fixture.Purchase.EntityId,
            PositionId=descendant.PositionId,LocationId=destination.EntityId,ConfirmedLocationCode=registeredCode,
            Quantity=1,Purpose="Fictional model",Reason="Reserve derived stock"
        },Guid.NewGuid(),token);
        var supplied = await new Receiving(fixture.Db).LossSnapshot(token,quarantine:true,recoveries:true,partOuts:true);
        var purchase = Assert.Single(supplied.Purchases,row=>row.Id==fixture.Purchase.EntityId);
        Assert.Equal(fixture.Delivery.EntityId,Assert.Single(purchase.Deliveries).Id);
        Assert.Contains(purchase.DerivedLayers!,row=>row.Id==descendant.DeliveryId && row.ItemCostMinor is null);
        var rival = chained with
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=hold.Revision,SourcePositionId=descendant.PositionId,
            SourceLocationId=destination.EntityId,ConfirmedSourceLocationCode=registeredCode,
            SourceCatalogId=retained.EntityId,SourceCatalogRevision=retained.Revision,ResidualItemCostMinor=null,
            Outputs=[chained.Outputs[0] with { ConfirmedLocationCode=registeredCode,CatalogId=child.EntityId,CatalogRevision=child.Revision }]
        };
        Assert.Equal("part_out_amount",await Reject(()=>Save(rival)));
        var released = await new BuildHolds(fixture.Db).Save(new StockHoldReleaseCommand
        {
            CommandId=Guid.NewGuid(),ExpectedRevision=hold.Revision,PurchaseId=fixture.Purchase.EntityId,
            Quantity=1,HoldId=hold.EntityId,Reason="Release specific derived hold"
        },Guid.NewGuid(),token);
        rival=rival with {ExpectedRevision=released.Revision};
        async Task<(Receipt? Receipt,string? Error)> TrySave(StockPartOutCommand choice)
        {
            try { return (await Save(choice),null); }
            catch (Fault fault) { return (null,fault.Code); }
        }

        var competitor=rival with {CommandId=Guid.NewGuid()};
        var claims=await Task.WhenAll(TrySave(rival),TrySave(competitor));
        var winner=Assert.Single(claims,result=>result.Receipt is not null).Receipt!;
        Assert.Equal("revision_conflict",Assert.Single(claims,result=>result.Receipt is null).Error);
        Assert.Equal(0,(await parts.Review(descendant.PositionId,1,null,25,token)).AvailableQuantity);
        Assert.Equal(winner,await Save(winner.CommandId==rival.CommandId?rival:competitor));
    }
}
