using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record SetContents([property: JsonRequired] bool IncludesBox, [property: JsonRequired] bool IncludesInstructions)
{
    internal short Code => (short)((IncludesBox ? 1 : 0) + (IncludesInstructions ? 2 : 0));
    internal static NpgsqlParameter Parameter(SetContents? contents) => Nullable(NpgsqlDbType.Smallint, contents?.Code);
    internal static SetContents? Read(NpgsqlDataReader reader, int column)
    {
        if (reader.IsDBNull(column)) return null;
        var code = reader.GetInt16(column);
        return new((code & 1) != 0, (code & 2) != 0);
    }

    internal static Fault Conflict(string name) => name switch
    {
        "set_contents_unverified" => new(409, name, "Inspect the box and instructions for every selected set position and confirm the draft contents."),
        "set_contents_not_set" => new(409, name, "Only a catalog set can record its box and instructions."),
        _ => new(409, name, "All selected sets must match the recorded box and instructions.")
    };
}

internal sealed record SetInspectionCommand
{
    public required Guid CommandId { get; init; }
    public required Guid PurchaseId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required Guid PositionId { get; init; }
    public required Guid LocationId { get; init; }
    public required string ConfirmedLocationCode { get; init; }
    public required Guid CatalogId { get; init; }
    public required int CatalogRevision { get; init; }
    public required int Quantity { get; init; }
    public required SetContents SetContents { get; init; }
    public required string Reason { get; init; }

    public void Validate()
    {
        if (CommandId == Guid.Empty || PurchaseId == Guid.Empty || PositionId == Guid.Empty || LocationId == Guid.Empty ||
            ExpectedRevision < 1 || CatalogId == Guid.Empty || CatalogRevision < 1 || Quantity is < 1 or > 1_000_000 ||
            SetContents is null || !Text(ConfirmedLocationCode,48) || !Text(Reason,200))
            throw new Fault(400,"invalid_set_inspection","Confirm the set, bin, quantity, box and instructions, and record a reason.");
    }
    private static bool Text(string? value, int limit) => value is { Length: > 0 } && value.Length<=limit &&
        value.Trim() == value && !value.Any(char.IsControl);
    public byte[] Digest() => SHA256.HashData(Encoding.UTF8.GetBytes("set-inspection\n" + JsonSerializer.Serialize(this, Wire.Json)));
}

internal sealed record SetPositionContents(string Scope, DateTime AsOf, bool Complete, Guid PositionId, Guid PurchaseId,
    int PurchaseRevision, Guid LocationId, Guid CatalogId, int CatalogRevision, int PhysicalQuantity,
    int FreeToClassifyQuantity, bool HasOpenShortage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SetContents? SetContents);

internal sealed record SetInspectionEvidence(string Scope, DateTime AsOf, bool Complete, Guid Id, Guid CommandId,
    Guid CorrelationId, int Revision, Guid PurchaseId, Guid PositionId, Guid ResultPositionId, Guid LocationId,
    string ConfirmedLocationCode, Guid CatalogId, int CatalogRevision, int Quantity, int PriorPhysicalQuantity,
    SetContents SetContents, string Reason, DateTime RecordedAt);

internal sealed class SetPositionInspections(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(SetInspectionCommand request, Guid correlation, CancellationToken token)
    {
        request.Validate();
        var digest = request.Digest();
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (await CommandJournal.Replay(connection,tx,request.CommandId,digest,token) is {} replay) return replay;
        var id = Guid.NewGuid();
        try
        {
            await using var insert = Query(connection,tx,"""
                INSERT INTO brickvault.set_position_inspections(id,command_id,purchase_id,expected_purchase_revision,
                    source_position_id,catalog_id,catalog_revision,location_id,location_code,quantity,set_contents,reason)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                """,id,request.CommandId,request.PurchaseId,request.ExpectedRevision,request.PositionId,request.CatalogId,
                request.CatalogRevision,request.LocationId,request.ConfirmedLocationCode,request.Quantity,request.SetContents.Code,request.Reason);
            await insert.ExecuteNonQueryAsync(token);
            var receipt = new Receipt(request.CommandId,id,checked(request.ExpectedRevision+1),"set-inspection",correlation);
            await CommandJournal.Append(connection,tx,receipt,digest,"record-receiving",token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException ex) when (ex.ConstraintName is "set_inspection_revision" or "set_inspection_identity"
            or "set_inspection_bin" or "set_inspection_quantity" or "set_inspection_committed" or "set_contents_already_recorded" or "stock_shortage_open")
        {
            throw ex.ConstraintName switch
            {
                "set_inspection_revision" => Fault.Conflict(),
                "stock_shortage_open" => Fault.StockShortageOpen(),
                "set_inspection_identity" => new Fault(409,ex.ConstraintName,"Review the current set, stock position and bin before inspecting."),
                "set_inspection_bin" => new Fault(409,ex.ConstraintName,"Confirm a registered label on the physical bin."),
                "set_contents_already_recorded" => new Fault(409,ex.ConstraintName,"This stock position already has recorded set contents."),
                "set_inspection_committed" => new Fault(409,ex.ConstraintName,"Only uncommitted units can be separated into another group."),
                _ => new Fault(409,ex.ConstraintName!,"Choose a positive inspection quantity within this stock position.")
            };
        }
    }

    public async Task<SetPositionContents> Review(Guid positionId, CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        await Migration.CheckWithin(connection,tx,token);
        return (await Rows(connection,tx,"""
            SELECT transaction_timestamp(),p.purchase_id,b.revision,p.location_id,p.catalog_id,p.catalog_revision,p.quantity,
                p.quantity-COALESCE((SELECT sum(quantity) FROM brickvault.current_offer_backing WHERE placement_id=p.id),0)
                    -COALESCE((SELECT sum(remaining_quantity) FROM brickvault.current_build_holds WHERE position_id=p.id),0)
                    -COALESCE((SELECT quantity FROM brickvault.current_position_quarantine WHERE position_id=p.id),0),
                EXISTS(SELECT 1 FROM brickvault.stock_shortage_cases WHERE position_id=p.id AND resolved_by_command_id IS NULL),
                observed.set_contents
            FROM brickvault.current_placement_stock p JOIN brickvault.purchases b ON b.id=p.purchase_id
            JOIN brickvault.catalog c ON c.id=p.catalog_id AND c.item_type='set'
            LEFT JOIN brickvault.position_set_contents observed ON observed.position_id=p.id WHERE p.id=$1
            """, r=>new SetPositionContents("set-position-contents",r.GetDateTime(0),true,positionId,r.GetGuid(1),
                r.GetInt32(2),r.GetGuid(3),r.GetGuid(4),r.GetInt32(5),r.GetInt32(6),checked((int)r.GetInt64(7)),
                r.GetBoolean(8),SetContents.Read(r,9)),token,positionId)).SingleOrDefault() ??
            throw new Fault(404,"unknown_set_position","This stock position does not identify a catalog set.");
    }

    public async Task<SetInspectionEvidence> Evidence(Guid positionId, Guid? inspectionId, Guid? commandId, CancellationToken token)
    {
        if (inspectionId.HasValue == commandId.HasValue)
            throw new Fault(400,"invalid_set_inspection_lookup","Select a single receipt or command identifier.");
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        await Migration.CheckWithin(connection,tx,token);
        return (await Rows(connection,tx,"""
            SELECT transaction_timestamp(),s.id,s.command_id,c.correlation_id,c.revision,s.purchase_id,s.source_position_id,
                s.result_position_id,s.location_id,s.location_code,s.catalog_id,s.catalog_revision,s.quantity,s.prior_quantity,
                s.set_contents,s.reason,a.occurred_at
            FROM brickvault.set_position_inspections s JOIN brickvault.commands c USING(command_id)
            JOIN brickvault.audit a USING(command_id) WHERE (s.source_position_id=$1 OR s.result_position_id=$1)
              AND (($2::uuid IS NOT NULL AND s.id=$2) OR ($3::uuid IS NOT NULL AND s.command_id=$3))
            """,r=>new SetInspectionEvidence("set-inspection",r.GetDateTime(0),true,r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),
                r.GetInt32(4),r.GetGuid(5),r.GetGuid(6),r.GetGuid(7),r.GetGuid(8),r.GetString(9),r.GetGuid(10),r.GetInt32(11),
                r.GetInt32(12),r.GetInt32(13),SetContents.Read(r,14)!,r.GetString(15),r.GetDateTime(16)),
            token,positionId,Nullable(NpgsqlDbType.Uuid,inspectionId),Nullable(NpgsqlDbType.Uuid,commandId))).SingleOrDefault() ??
            throw new Fault(404,"unknown_set_inspection","No set inspection matches that position and receipt.");
    }
}

internal static class SetDetailRoutes
{
    internal static RouteHandlerBuilder WithSetDetails(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (context,next) =>
        {
            var result = await next(context);
            if (SetContentsContract.Negotiate(context.HttpContext)) return result;
            bool HasPositionFacts(IReadOnlyList<PlacementRow> positions) => positions.Any(p=>p.SetContents is not null);
            bool HasDeliveryFacts(LossDeliveryRow delivery) => delivery.SetContents is not null || HasPositionFacts(delivery.Placements);
            var requiresCurrent = result switch
            {
                ReceivingWorkspace page => page.Purchases.Any(p=>p.Offers.Any(o=>o.SetContents is not null) ||
                    p.Deliveries.Any(d=>d.SetContents is not null || HasPositionFacts(d.Placements))),
                LossReceivingWorkspace page => page.Purchases.Any(p=>p.Offers.Any(o=>o.SetContents is not null) ||
                    p.Deliveries.Any(HasDeliveryFacts) || p.DerivedLayers?.Any(HasDeliveryFacts) == true),
                BinStockSnapshot page => page.Layers.Any(l=>l.SetContents is not null),
                BinTransferSnapshot page => page.Positions.Any(p=>p.Layer.SetContents is not null),
                StockRegisterPage page => page.Rows.Any(p=>p.Layer.SetContents is not null),
                DraftBackingSnapshot page => page.Position.Layer.SetContents is not null || page.Offers.Any(o=>o.SetContents is not null),
                DraftBackingHistory page => page.Position.Layer.SetContents is not null || page.Offer.SetContents is not null,
                DraftBackingWithdrawalEvidence evidence => evidence.SetContents is not null,
                StockQuarantineSnapshot page => page.Position.Layer.SetContents is not null,
                StockPartOutReview page => page.Position.Layer.SetContents is not null,
                StockLossRecoveryReview page => page.Position.Layer.SetContents is not null,
                StockLossRecoveryEvidence page => page.SetContents is not null || page.ResultPositionId is not null,
                StockPartOutEvidence page => page.Outputs.Any(o=>o.SetContents is not null),
                StockPositionTimeline page => page.Position.Layer.SetContents is not null || page.Events.Any(e=>(e.Kind == "set-inspection" || (e.Kind == "stock-loss-recovery" && e.RelatedPositionId.HasValue))),
                StockReceiptLot page => page.Positions.Any(p=>p.Layer.SetContents is not null) || page.Events.Any(e=>(e.Event.Kind == "set-inspection" || (e.Event.Kind == "stock-loss-recovery" && e.Event.RelatedPositionId.HasValue))),
                SetPositionContents or SetInspectionEvidence => true,
                _ => false
            };
            if (requiresCurrent) throw SetContentsContract.Upgrade();
            return result;
        });
}
