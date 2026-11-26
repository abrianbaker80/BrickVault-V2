using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record StockPartOutOutputInput([property: JsonRequired] Guid LineId, [property: JsonRequired] Guid CatalogId,
    [property: JsonRequired] int CatalogRevision, [property: JsonRequired] Guid LocationId,
    [property: JsonRequired] string ConfirmedLocationCode, [property: JsonRequired] int Quantity,
    [property: JsonRequired] int? ItemCostMinor, [property: JsonRequired] string Disposition);
internal sealed record StockPartOutPhysicalResidual([property: JsonRequired] Guid LineId, [property: JsonRequired] string Disposition,
    [property: JsonRequired] string Description, [property: JsonRequired] int Quantity);

internal sealed record StockPartOutCommand
{
    public required Guid CommandId { get; init; }
    public required Guid PurchaseId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required Guid SourcePositionId { get; init; }
    public required Guid SourceLocationId { get; init; }
    public required string ConfirmedSourceLocationCode { get; init; }
    public required Guid SourceCatalogId { get; init; }
    public required int SourceCatalogRevision { get; init; }
    public required int Quantity { get; init; }
    public required string Reason { get; init; }
    public required StockPartOutOutputInput[] Outputs { get; init; }
    public required int? ResidualItemCostMinor { get; init; }
    public required StockPartOutPhysicalResidual[] PhysicalResiduals { get; init; }

    public void Validate()
    {
        if (CommandId==Guid.Empty || PurchaseId==Guid.Empty || ExpectedRevision<1 || SourcePositionId==Guid.Empty ||
            SourceLocationId==Guid.Empty || SourceCatalogId==Guid.Empty || SourceCatalogRevision<1 || Quantity is <1 or >1_000_000 ||
            !TextFits(ConfirmedSourceLocationCode,48) || !TextFits(Reason,200) || ResidualItemCostMinor is <0 or >100_000_000 ||
            Outputs is not {Length:>0 and <=1_000_000} || PhysicalResiduals is null || PhysicalResiduals.Length>1_000_000)
            throw Invalid();
        HashSet<Guid> lineIds=[];
        var remaining=1_000_000;
        foreach (var line in Outputs)
        {
            if (line is null || line.LineId==Guid.Empty || !lineIds.Add(line.LineId) || line.CatalogId==Guid.Empty ||
                line.CatalogRevision<1 || line.LocationId==Guid.Empty || !TextFits(line.ConfirmedLocationCode,48) ||
                line.Quantity<1 || line.Quantity>remaining || line.ItemCostMinor is <0 or >100_000_000 ||
                line.Disposition is not ("stock" or "retained")) throw Invalid();
            remaining-=line.Quantity;
        }
        lineIds.Clear();
        remaining=1_000_000;
        foreach (var line in PhysicalResiduals)
        {
            if (line is null || line.LineId==Guid.Empty || !lineIds.Add(line.LineId) || line.Disposition is not ("discarded" or "lost") ||
                !TextFits(line.Description,200) || line.Quantity<1 || line.Quantity>remaining) throw Invalid();
            remaining-=line.Quantity;
        }
    }
    private static bool TextFits(string? value,int limit) => value is {Length:>0} &&
        value.Length<=limit && value==value.Trim() && !value.Any(char.IsControl);
    private static Fault Invalid() => new(400,"invalid_stock_part_out",
        "Confirm the source and outputs, choose their quantities and disposition, and specify the documented basis and residual.");
    public byte[] Digest()
    {
        using var hash=SHA256.Create();
        using var stream=new CryptoStream(Stream.Null,hash,CryptoStreamMode.Write);
        stream.Write("part-out\n"u8);
        JsonSerializer.Serialize(stream,this with
        {
            Outputs=Outputs.OrderBy(line=>line.LineId).ToArray(),
            PhysicalResiduals=PhysicalResiduals.OrderBy(line=>line.LineId).ToArray()
        },Wire.Json);
        stream.FlushFinalBlock();
        return hash.Hash!;
    }
}

internal sealed record StockPartOutSummary(Guid Id,Guid CommandId,DateTime RecordedAt,int Quantity,
    int? SourceItemCostMinor,int? ResidualItemCostMinor,int OutputCount);
internal sealed record StockPartOutReview(string Scope,DateTime AsOf,bool Complete,StockRegisterRow Position,int PurchaseRevision,
    bool HasOpenShortage,int HeldQuantity,int AvailableQuantity,int RequestedQuantity,bool SelectionAvailable,int? SourceItemCostMinor,
    string Currency,IReadOnlyList<StockPartOutSummary> PartOuts,Guid? NextAfter);
internal sealed record StockPartOutOutput(Guid LineId,Guid DeliveryId,Guid PositionId,Guid CommandId,Guid CorrelationId,
    CatalogRow RecordedCatalog,LocationRow Location,string ConfirmedLocationCode,int Quantity,int? ItemCostMinor,
    string Disposition,Guid? QuarantineId);
internal sealed record StockPartOutEvidence(string Scope,DateTime AsOf,bool Complete,Guid Id,Guid CommandId,Guid CorrelationId,
    int Revision,Guid PurchaseId,Guid SourcePositionId,LocationRow SourceLocation,string ConfirmedSourceLocationCode,
    CatalogRow RecordedSourceCatalog,AdjustmentReceiptEvidence Receipt,int Quantity,string Reason,DateTime RecordedAt,
    int? SourceItemCostMinor,int? ResidualItemCostMinor,IReadOnlyList<StockPartOutOutput> Outputs,
    IReadOnlyList<StockPartOutPhysicalResidual> PhysicalResiduals,string? NextOutputAfter,string? NextResidualAfter);

internal sealed class StockPartOuts(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(StockPartOutCommand command,Guid correlation,CancellationToken token)
    {
        command.Validate();
        var digest=command.Digest();
        await using var connection=await db.OpenConnectionAsync(token);
        await using var tx=await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection,tx,token);
        if (await CommandJournal.Replay(connection,tx,command.CommandId,digest,token) is { } replay) return replay;
        var id=Guid.NewGuid();
        try
        {
            await using (var insert=Query(connection,tx,"""
                INSERT INTO brickvault.stock_part_outs(id,command_id,purchase_id,source_position_id,source_location_id,
                    source_location_code,source_catalog_id,source_catalog_revision,expected_purchase_revision,quantity,reason,residual_item_cost_minor)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                """,id,command.CommandId,command.PurchaseId,command.SourcePositionId,command.SourceLocationId,
                command.ConfirmedSourceLocationCode,command.SourceCatalogId,command.SourceCatalogRevision,
                command.ExpectedRevision,command.Quantity,command.Reason,Nullable(NpgsqlDbType.Integer,command.ResidualItemCostMinor)))
                await insert.ExecuteNonQueryAsync(token);

            var outputs=command.Outputs;
            await using (var insert=Query(connection,tx,"""
                INSERT INTO brickvault.stock_part_out_outputs(part_out_id,line_id,catalog_id,catalog_revision,location_id,location_code,
                    quantity,item_cost_minor,disposition)
                SELECT $1,line_id,catalog_id,catalog_revision,location_id,location_code,quantity,item_cost_minor,disposition
                FROM unnest($2::uuid[],$3::uuid[],$4::integer[],$5::uuid[],$6::text[],$7::integer[],$8::integer[],$9::text[])
                    AS choices(line_id,catalog_id,catalog_revision,location_id,location_code,quantity,item_cost_minor,disposition)
                ORDER BY line_id
                """,id,outputs.Select(o=>o.LineId).ToArray(),outputs.Select(o=>o.CatalogId).ToArray(),
                outputs.Select(o=>o.CatalogRevision).ToArray(),outputs.Select(o=>o.LocationId).ToArray(),
                outputs.Select(o=>o.ConfirmedLocationCode).ToArray(),outputs.Select(o=>o.Quantity).ToArray(),
                new NpgsqlParameter {NpgsqlDbType=NpgsqlDbType.Array|NpgsqlDbType.Integer,Value=outputs.Select(o=>o.ItemCostMinor).ToArray()},
                outputs.Select(o=>o.Disposition).ToArray()))
                await insert.ExecuteNonQueryAsync(token);

            if (command.PhysicalResiduals.Length>0)
            {
                var residuals=command.PhysicalResiduals;
                await using var insert=Query(connection,tx,"""
                    INSERT INTO brickvault.stock_part_out_residuals(part_out_id,line_id,disposition,description,quantity)
                    SELECT $1,line_id,disposition,description,quantity
                    FROM unnest($2::uuid[],$3::text[],$4::text[],$5::integer[]) AS residual(line_id,disposition,description,quantity)
                    """,id,residuals.Select(r=>r.LineId).ToArray(),residuals.Select(r=>r.Disposition).ToArray(),
                    residuals.Select(r=>r.Description).ToArray(),residuals.Select(r=>r.Quantity).ToArray());
                await insert.ExecuteNonQueryAsync(token);
            }
            var receipt=new Receipt(command.CommandId,id,checked(command.ExpectedRevision+1),"part-out",correlation);
            await CommandJournal.Append(connection,tx,receipt,digest,"record-receiving",token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException error) when (error.ConstraintName is "part_out_revision" or "part_out_scope" or "part_out_bin" or
            "part_out_catalog" or "part_out_amount" or "part_out_identity_gain" or "part_out_cost_mismatch" or "stock_shortage_open")
        {
            throw error.ConstraintName switch
            {
                "part_out_revision"=>Fault.Conflict(),
                "part_out_scope"=>new Fault(409,"part_out_scope","Choose a current source position from the specified purchase and bin."),
                "part_out_bin"=>new Fault(409,"part_out_bin","Confirm the current registered code for the source and each output bin."),
                "part_out_catalog"=>new Fault(409,"part_out_catalog","Review the current source identity and each eligible output identity."),
                "part_out_identity_gain"=>new Fault(409,"part_out_identity_gain",
                    "Outputs with the same catalog identity as the input cannot exceed the input quantity."),
                "part_out_cost_mismatch"=>new Fault(409,"part_out_cost_mismatch",
                    "Document output basis and residual that conserve the selected source basis. Preserve unknown amounts."),
                "stock_shortage_open"=>new Fault(409,"stock_shortage_open","Review the open shortage before consuming this stock."),
                _=>new Fault(409,"part_out_amount","Choose available uncommitted source stock and positive bounded output quantities.")
            };
        }
    }

    internal static Fault Upgrade() => new(426,"stock_part_out_client_upgrade_required",
        "Update Workbench to read part-out layers and their original source.");
    private static Fault InvalidCursor() => new(400,"invalid_stock_part_out_cursor","Use a continuation from the selected part-out read.");
    private static void CheckLimit(int limit)
    {
        if (limit is <1 or >100) throw new Fault(400,"invalid_stock_part_out_page","Choose a page size from 1 to 100.");
    }
    private static async Task BeginRead(NpgsqlConnection connection,NpgsqlTransaction tx,CancellationToken token)
    {
        await using var mode=Query(connection,tx,"SET TRANSACTION READ ONLY");
        await mode.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection,tx,token);
    }
    private static Guid? Next<T>(List<T> rows,int limit,Func<T,Guid> key)
    {
        if (rows.Count<=limit) return null;
        rows.RemoveAt(limit);
        return key(rows[^1]);
    }

    public async Task<StockPartOutReview> Review(Guid positionId,int quantity,Guid? after,int limit,CancellationToken token)
    {
        CheckLimit(limit);
        if (quantity is <1 or >1_000_000) throw new Fault(400,"invalid_stock_part_out","Choose a positive source quantity.");
        await using var connection=await db.OpenConnectionAsync(token);
        await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        await BeginRead(connection,tx,token);
        var positions=await Rows(connection,tx,"SELECT "+PositionRows.RegisterSelect+",transaction_timestamp()\n"+
            PositionRows.RegisterFrom+"\nWHERE p.id=$1",r=>(Position:PositionRows.ReadRegister(r,true),
                Revision:r.GetInt32(29),Shortage:r.GetBoolean(31),Held:r.GetInt32(32),AsOf:r.GetDateTime(39)),token,positionId);
        if (positions.Count==0) throw new Fault(404,"unknown_position","No stock position has that identifier.");
        var position=positions.Single();
        var layer=position.Position.Layer;
        var available=position.Shortage?0:layer.Quantity-position.Held-layer.BackedQuantity-layer.QuarantinedQuantity.GetValueOrDefault();
        var selected=quantity<=available;
        int? basis=null;
        if (selected)
        {
            await using var value=Query(connection,tx,"""
                SELECT brickvault.basis_slot_amount(brickvault.slice_basis_slots(brickvault.open_stock_basis_slots(d.id),0,$2),
                    d.quantity,d.item_cost_minor) FROM brickvault.deliveries d WHERE d.id=$1
                """,layer.DeliveryId,quantity);
            basis=(await value.ExecuteScalarAsync(token)) as int?;
        }
        int? previous=null;
        if (after.HasValue)
        {
            await using var anchor=Query(connection,tx,"""
                SELECT expected_purchase_revision FROM brickvault.stock_part_outs WHERE source_position_id=$1 AND id=$2
                """,positionId,after.Value);
            previous=(await anchor.ExecuteScalarAsync(token)) as int? ?? throw InvalidCursor();
        }
        var history=await Rows(connection,tx,"""
            SELECT p.id,p.command_id,a.occurred_at,p.quantity,p.source_item_cost_minor,p.residual_item_cost_minor,
                (SELECT count(*) FROM brickvault.stock_part_out_outputs o WHERE o.part_out_id=p.id)::integer
            FROM brickvault.stock_part_outs p JOIN brickvault.audit a ON a.command_id=p.command_id
            WHERE p.source_position_id=$1 AND ($2::integer IS NULL OR p.expected_purchase_revision<$2)
            ORDER BY p.expected_purchase_revision DESC LIMIT $3
            """,r=>new StockPartOutSummary(r.GetGuid(0),r.GetGuid(1),r.GetDateTime(2),r.GetInt32(3),Optional<int>(r,4),
                Optional<int>(r,5),r.GetInt32(6)),token,positionId,Nullable(NpgsqlDbType.Integer,previous),limit+1);
        return new StockPartOutReview("part-out-review",position.AsOf,true,position.Position,position.Revision,position.Shortage,
            position.Held,available,quantity,selected,basis,layer.Currency,history,Next(history,limit,row=>row.Id));
    }

    public async Task<StockPartOutEvidence> Evidence(Guid sourcePositionId,Guid? partOutId,Guid? commandId,string? outputAfter,int outputLimit,
        string? residualAfter,int residualLimit,CancellationToken token)
    {
        CheckLimit(outputLimit);
        CheckLimit(residualLimit);
        if (partOutId.HasValue==commandId.HasValue)
            throw new Fault(400,"invalid_stock_part_out_lookup","Choose exactly one part-out receipt or command.");
        await using var connection=await db.OpenConnectionAsync(token);
        await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        await BeginRead(connection,tx,token);
        var selector=partOutId.HasValue?"p.id=$2":"p.command_id=$2";
        var evidence=(await Rows(connection,tx,"""
            SELECT transaction_timestamp(),p.id,p.command_id,c.correlation_id,c.revision,p.purchase_id,
                loc.id,loc.revision,loc.code,loc.name,p.source_location_code,p.recorded_source_catalog,p.quantity,p.reason,a.occurred_at,
                p.source_item_cost_minor,p.residual_item_cost_minor,d.id,d.reference,d.quantity,d.item_cost_minor,buy.currency,
                stock.origin_placement_id,stock.catalog_id,stock.catalog_revision
            FROM brickvault.stock_part_outs p JOIN brickvault.commands c USING(command_id) JOIN brickvault.audit a USING(command_id)
            JOIN brickvault.locations loc ON loc.id=p.source_location_id JOIN brickvault.purchases buy ON buy.id=p.purchase_id
            JOIN brickvault.deliveries d ON d.id=p.source_delivery_id JOIN brickvault.placement_positions stock ON stock.id=p.source_position_id
            WHERE p.source_position_id=$1 AND
            """+" "+selector,r=>new StockPartOutEvidence("part-out-receipt",r.GetDateTime(0),true,r.GetGuid(1),r.GetGuid(2),
                r.GetGuid(3),r.GetInt32(4),r.GetGuid(5),sourcePositionId,new LocationRow(r.GetGuid(6),r.GetInt32(7),r.GetString(8),r.GetString(9)),
                r.GetString(10),JsonSerializer.Deserialize<CatalogRow>(r.GetString(11),Wire.Json)!,
                new AdjustmentReceiptEvidence(r.GetGuid(17),r.GetString(18),r.GetInt32(19),Optional<int>(r,20),r.GetString(21),
                    r.GetGuid(22),r.GetGuid(23),r.GetInt32(24)),
                r.GetInt32(12),r.GetString(13),r.GetDateTime(14),Optional<int>(r,15),Optional<int>(r,16),[],[],null,null),
            token,sourcePositionId,(partOutId??commandId)!.Value)).SingleOrDefault() ??
            throw new Fault(404,"unknown_stock_part_out","No part-out receipt matches this source position and identifier.");

        var outputAnchor=ParseCursor(outputAfter,"out",evidence.Id);
        if (outputAnchor.HasValue)
        {
            await using var anchor=Query(connection,tx,"SELECT 1 FROM brickvault.stock_part_out_outputs WHERE part_out_id=$1 AND line_id=$2",evidence.Id,outputAnchor.Value);
            if (await anchor.ExecuteScalarAsync(token) is null) throw InvalidCursor();
        }
        var residualAnchor=ParseCursor(residualAfter,"res",evidence.Id);
        if (residualAnchor.HasValue)
        {
            await using var anchor=Query(connection,tx,"SELECT 1 FROM brickvault.stock_part_out_residuals WHERE part_out_id=$1 AND line_id=$2",evidence.Id,residualAnchor.Value);
            if (await anchor.ExecuteScalarAsync(token) is null) throw InvalidCursor();
        }
        var outputs=await Rows(connection,tx,"""
            SELECT o.line_id,o.delivery_id,o.position_id,c.command_id,c.correlation_id,o.recorded_catalog,l.id,l.revision,l.code,l.name,
                o.location_code,o.quantity,o.item_cost_minor,o.disposition
            FROM brickvault.stock_part_out_outputs o JOIN brickvault.commands c ON c.command_id=o.child_command_id
            JOIN brickvault.locations l ON l.id=o.location_id
            WHERE o.part_out_id=$1 AND ($2::uuid IS NULL OR o.line_id>$2) ORDER BY o.line_id LIMIT $3
            """,r=>new StockPartOutOutput(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),r.GetGuid(4),
                JsonSerializer.Deserialize<CatalogRow>(r.GetString(5),Wire.Json)!,
                new LocationRow(r.GetGuid(6),r.GetInt32(7),r.GetString(8),r.GetString(9)),r.GetString(10),r.GetInt32(11),
                Optional<int>(r,12),r.GetString(13),r.GetString(13)=="retained"?r.GetGuid(2):null),
            token,evidence.Id,Nullable(NpgsqlDbType.Uuid,outputAnchor),outputLimit+1);
        var residuals=await Rows(connection,tx,"""
            SELECT line_id,disposition,description,quantity FROM brickvault.stock_part_out_residuals
            WHERE part_out_id=$1 AND ($2::uuid IS NULL OR line_id>$2) ORDER BY line_id LIMIT $3
            """,r=>new StockPartOutPhysicalResidual(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetInt32(3)),
            token,evidence.Id,Nullable(NpgsqlDbType.Uuid,residualAnchor),residualLimit+1);
        var outputNext=Next(outputs,outputLimit,row=>row.LineId);
        var residualNext=Next(residuals,residualLimit,row=>row.LineId);
        return evidence with
        {
            Outputs=outputs,PhysicalResiduals=residuals,
            NextOutputAfter=outputNext.HasValue?$"out.{evidence.Id:N}.{outputNext:N}":null,
            NextResidualAfter=residualNext.HasValue?$"res.{evidence.Id:N}.{residualNext:N}":null
        };
    }

    private static Guid? ParseCursor(string? cursor,string kind,Guid id)
    {
        if (cursor is null) return null;
        if (cursor.Length!=69) throw InvalidCursor();
        var parts=cursor.Split('.');
        if (parts.Length!=3 || parts[0]!=kind || !Guid.TryParseExact(parts[1],"N",out var scope) ||
            scope!=id || !Guid.TryParseExact(parts[2],"N",out var line)) throw InvalidCursor();
        return line;
    }
}
