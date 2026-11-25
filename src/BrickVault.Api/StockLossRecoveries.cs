using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record StockLossRecoveryCommand
{
    public required Guid CommandId { get; init; }
    public required Guid PurchaseId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required Guid PositionId { get; init; }
    public required Guid LocationId { get; init; }
    public required string ConfirmedLocationCode { get; init; }
    public required Guid PostingId { get; init; }
    public required Guid CatalogId { get; init; }
    public required int CatalogRevision { get; init; }
    public required bool ConfirmedIdentity { get; init; }
    public required string Reason { get; init; }
    public required StockLossRecoverySelection[] Allocations { get; init; }

    public void Validate()
    {
        if (CommandId == Guid.Empty || PurchaseId == Guid.Empty || PositionId == Guid.Empty || LocationId == Guid.Empty ||
            PostingId == Guid.Empty || CatalogId == Guid.Empty || ExpectedRevision < 1 || CatalogRevision < 1 ||
            !ConfirmedIdentity || !TextFits(ConfirmedLocationCode, 48) || !TextFits(Reason, 200) ||
            Allocations is not { Length: > 0 and <= 1_000_000 }) throw Invalid();
        HashSet<Guid?> sources = [];
        var remaining = 1_000_000;
        foreach (var allocation in Allocations)
        {
            if (allocation is null || allocation.QuarantineId == Guid.Empty || !sources.Add(allocation.QuarantineId) ||
                allocation.SaleableQuantity < 0 || allocation.QuarantineQuantity < 0 ||
                allocation.SaleableQuantity > remaining || allocation.QuarantineQuantity > remaining-allocation.SaleableQuantity ||
                allocation.SaleableQuantity+allocation.QuarantineQuantity == 0) throw Invalid();
            remaining -= allocation.SaleableQuantity+allocation.QuarantineQuantity;
        }
    }

    private static Fault Invalid() => new(400, "invalid_stock_loss_recovery",
        "Confirm the receipt identity and bin, and choose inspected and quarantined amounts for each named source.");

    private static bool TextFits(string? value, int limit) => value is { Length: > 0 } &&
        value.Length <= limit && value == value.Trim() && !value.Any(char.IsControl);

    public byte[] Digest()
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        stream.Write("stock-loss-recovery\n"u8);
        JsonSerializer.Serialize(stream, this with { Allocations = Allocations.OrderBy(a => a.QuarantineId).ToArray() }, Wire.Json);
        stream.FlushFinalBlock();
        return hash.Hash!;
    }
}

internal sealed record StockLossRecoverySelection([property: JsonRequired] Guid? QuarantineId,
    [property: JsonRequired] int SaleableQuantity, [property: JsonRequired] int QuarantineQuantity);
internal sealed record StockLossRecoverySource(Guid? QuarantineId, int OriginalQuantity, int RecoveredQuantity, int RemainingQuantity,
    int? OriginalItemCostMinor, int? RestoredItemCostMinor, int? RemainingItemCostMinor, string? OriginalQuarantineReason);
internal sealed record StockLossRecoveryHistory(Guid Id, Guid CommandId, DateTime RecordedAt, int SaleableQuantity,
    int QuarantineQuantity, int? RestoredItemCostMinor, Guid? RecoveredQuarantineId);
internal sealed record StockLossRecoveryAllocation(Guid? QuarantineId, int SaleableQuantity, int QuarantineQuantity, int? RestoredItemCostMinor);

internal sealed record StockLossRecoveryReview(string Scope, DateTime AsOf, bool Complete, Guid PostingId,
    int PurchaseRevision, StockRegisterRow Position, AdjustmentReceiptEvidence Receipt, bool HasOpenShortage,
    int OriginalLostQuantity, int RecoveredQuantity, int RemainingQuantity, int? OriginalAllocatedItemCostMinor,
    int? RestoredItemCostMinor, int? RemainingLostItemCostMinor, string OriginalReason, DateTime OriginalPostedAt,
    IReadOnlyList<StockLossRecoverySource> Sources, string? NextAfter,
    IReadOnlyList<StockLossRecoveryHistory> Recoveries, Guid? NextHistoryAfter);

internal sealed record StockLossRecoveryEvidence(string Scope, DateTime AsOf, bool Complete, Guid Id, Guid CommandId,
    Guid CorrelationId, int Revision, Guid PurchaseId, Guid PositionId, Guid PostingId, LocationRow Location,
    string ConfirmedLocationCode, CatalogRow RecordedCatalog, AdjustmentReceiptEvidence Receipt,
    string Reason, DateTime RecordedAt, int SaleableQuantity, int QuarantineQuantity, int? RestoredItemCostMinor,
    Guid? RecoveredQuarantineId, IReadOnlyList<StockLossRecoveryAllocation> Allocations, string? NextAfter);

internal sealed class StockLossRecoveries(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(StockLossRecoveryCommand command, Guid correlationId, CancellationToken token)
    {
        command.Validate();
        var digest = command.Digest();
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (await CommandJournal.Replay(connection, tx, command.CommandId, digest, token) is { } replay) return replay;
        var id = Guid.NewGuid();
        try
        {
            await using (var receiptRow = Query(connection, tx, """
                INSERT INTO brickvault.stock_loss_recoveries(id,command_id,purchase_id,position_id,location_id,location_code,loss_id,
                    expected_purchase_revision,catalog_id,catalog_revision,confirmed_identity,reason)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                """, id, command.CommandId, command.PurchaseId, command.PositionId, command.LocationId,
                command.ConfirmedLocationCode, command.PostingId, command.ExpectedRevision,
                command.CatalogId, command.CatalogRevision, command.ConfirmedIdentity, command.Reason))
                await receiptRow.ExecuteNonQueryAsync(token);
            await using (var lines = Query(connection, tx, """
                INSERT INTO brickvault.stock_loss_recovery_allocations(recovery_id,quarantine_id,saleable_quantity,quarantine_quantity)
                SELECT $1,nullif(source,'00000000-0000-0000-0000-000000000000'),saleable,quarantined
                FROM unnest($2::uuid[],$3::integer[],$4::integer[]) AS selected(source,saleable,quarantined)
                """, id, command.Allocations.Select(a => a.QuarantineId ?? Guid.Empty).ToArray(),
                command.Allocations.Select(a => a.SaleableQuantity).ToArray(),
                command.Allocations.Select(a => a.QuarantineQuantity).ToArray()))
                await lines.ExecuteNonQueryAsync(token);
            var receipt = new Receipt(command.CommandId, id, checked(command.ExpectedRevision+1), "stock-loss-recovery", correlationId);
            await CommandJournal.Append(connection, tx, receipt, digest, "record-receiving", token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException error) when (error.ConstraintName is "stock_loss_recovery_revision" or "stock_loss_recovery_scope" or
            "stock_loss_recovery_bin" or "stock_loss_recovery_catalog" or "stock_loss_recovery_amount" or
            "stock_loss_recovery_allocations_quarantine_id_fkey")
        {
            throw error.ConstraintName switch
            {
                "stock_loss_recovery_revision" => Fault.Conflict(),
                "stock_loss_recovery_scope" => Conflict(error.ConstraintName, "Choose the exact original posted loss at this receipt position and bin."),
                "stock_loss_recovery_bin" => Conflict(error.ConstraintName, "Confirm the current registered code for the original bin."),
                "stock_loss_recovery_catalog" => Conflict(error.ConstraintName, "Reload and physically confirm the current catalog identity and revision."),
                _ => Conflict("stock_loss_recovery_amount", "Choose quantities still missing from the named original loss sources.")
            };
        }
    }

    private static Fault Conflict(string code, string message) => new(409, code, message);
    internal static Fault Upgrade() => new(426, "stock_loss_recovery_client_upgrade_required",
        "Update Workbench to read recovered stock and its original loss history.");
    private static Fault InvalidCursor() => new(400, "invalid_stock_loss_recovery_cursor",
        "Use a continuation from this loss recovery read.");

    private static void CheckLimit(int limit)
    {
        if (limit is < 1 or > 100)
            throw new Fault(400, "invalid_stock_loss_recovery_page", "Choose a page size from 1 to 100.");
    }

    private static async Task BeginRead(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken token)
    {
        await using var mode = Query(connection, tx, "SET TRANSACTION READ ONLY");
        await mode.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection, tx, token);
    }

    public async Task<StockLossRecoveryReview> Review(Guid locationId, Guid positionId, Guid postingId, string? after, int limit,
        Guid? historyAfter, int historyLimit, CancellationToken token)
    {
        CheckLimit(limit);
        CheckLimit(historyLimit);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await BeginRead(connection, tx, token);

        var positions = await Rows(connection, tx, "SELECT " + PositionRows.RegisterSelect + "\n" + PositionRows.RegisterFrom +
            "\nWHERE p.id=$1 AND p.location_id=$2", r => (Value:PositionRows.ReadRegister(r,true), Revision:r.GetInt32(29),
                Shortage:r.GetBoolean(31)), token, positionId, locationId);
        var position = positions.SingleOrDefault();
        var original = (await Rows(connection, tx, """
            SELECT transaction_timestamp(),l.lost_quantity,l.allocated_item_cost_minor,l.reason,a.occurred_at,
                COALESCE(returned.quantity,0)::integer,CASE WHEN d.item_cost_minor IS NOT NULL THEN COALESCE(returned.basis,0)::integer END,
                d.id,d.reference,d.quantity,d.item_cost_minor,p.currency,stock.origin_placement_id,stock.catalog_id,stock.catalog_revision
            FROM brickvault.stock_loss_postings l
            JOIN brickvault.audit a ON a.command_id=l.command_id
            JOIN brickvault.deliveries d ON d.id=l.delivery_id JOIN brickvault.purchases p ON p.id=l.purchase_id
            JOIN brickvault.placement_positions stock ON stock.id=l.position_id
            LEFT JOIN LATERAL (
                SELECT sum(r.saleable_quantity+r.quarantine_quantity) AS quantity,sum(r.restored_item_cost_minor) AS basis
                FROM brickvault.stock_loss_recoveries r WHERE r.loss_id=l.id
            ) returned ON true
            WHERE l.id=$1 AND l.position_id=$2 AND l.location_id=$3
            """, r => (AsOf:r.GetDateTime(0), Lost:r.GetInt32(1), Basis:Optional<int>(r,2), Reason:r.GetString(3),
                At:r.GetDateTime(4), Recovered:r.GetInt32(5), Restored:Optional<int>(r,6), Receipt:ReadReceipt(r,7)),
            token, postingId, positionId, locationId)).SingleOrDefault();
        if (position.Value is null || original.Receipt is null)
            throw new Fault(404, "unknown_stock_loss", "No matching posted loss belongs to this position and bin.");

        Guid? anchor = null;
        if (after is not null)
        {
            if (after.Length > 76) throw InvalidCursor();
            var parts = after.Split('.');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[0],"N",out var scope) || scope!=postingId ||
                !int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var revision) ||
                !Guid.TryParseExact(parts[2],"N",out var id)) throw InvalidCursor();
            if (revision != position.Revision)
                throw Conflict("stale_stock_loss_recovery_page", "This purchase changed. Reload the loss review before continuing.");
            if (id != Guid.Empty)
            {
                await using var match = Query(connection, tx, """
                    SELECT 1 FROM brickvault.stock_loss_quarantine_attributions WHERE loss_id=$1 AND quarantine_id=$2
                    """, postingId, id);
                if (await match.ExecuteScalarAsync(token) is null) throw InvalidCursor();
            }
            anchor = id;
        }

        var sourceRows = await Rows(connection, tx, """
            WITH page AS (
                SELECT s.* FROM brickvault.stock_loss_recovery_sources($1) s
                WHERE $2::uuid IS NULL OR s.quarantine_id>$2
                ORDER BY s.quarantine_id NULLS FIRST LIMIT $3
            )
            SELECT s.quarantine_id,s.original_quantity,COALESCE(returned.quantity,0)::integer,
                brickvault.basis_slot_amount(brickvault.slice_basis_slots(original.basis_slots,s.source_offset,s.original_quantity),$4,$5),
                CASE WHEN $5::integer IS NOT NULL THEN COALESCE(returned.basis,0)::integer END,q.reason
            FROM page s JOIN brickvault.original_stock_loss_basis original ON original.loss_id=$1
            LEFT JOIN brickvault.stock_quarantines q ON q.id=s.quarantine_id
            LEFT JOIN LATERAL (
                SELECT sum(a.saleable_quantity+a.quarantine_quantity) AS quantity,sum(a.restored_item_cost_minor) AS basis
                FROM brickvault.stock_loss_recoveries r JOIN brickvault.stock_loss_recovery_allocations a ON a.recovery_id=r.id
                WHERE r.loss_id=$1 AND a.quarantine_id IS NOT DISTINCT FROM s.quarantine_id
            ) returned ON true
            ORDER BY s.quarantine_id NULLS FIRST
            """, r =>
        {
            var initial=r.GetInt32(1); var recovered=r.GetInt32(2);
            var basis=Optional<int>(r,3); var reversed=Optional<int>(r,4);
            return new StockLossRecoverySource(Optional<Guid>(r,0),initial,recovered,initial-recovered,basis,reversed,basis-reversed,
                r.IsDBNull(5)?null:r.GetString(5));
        }, token, postingId, Nullable(NpgsqlDbType.Uuid,anchor), limit+1, original.Receipt.DeliveryQuantity,
            Nullable(NpgsqlDbType.Integer,original.Receipt.ItemCostMinor));

        string? next = null;
        if (sourceRows.Count > limit)
        {
            sourceRows.RemoveAt(limit);
            next = FormattableString.Invariant($"{postingId:N}.{position.Revision}.{sourceRows[^1].QuarantineId ?? Guid.Empty:N}");
        }
        int? previous = null;
        if (historyAfter.HasValue)
        {
            await using var match = Query(connection, tx, """
                SELECT expected_purchase_revision FROM brickvault.stock_loss_recoveries WHERE loss_id=$1 AND id=$2
                """, postingId, historyAfter.Value);
            previous = (int?)await match.ExecuteScalarAsync(token) ?? throw InvalidCursor();
        }
        var history = await Rows(connection, tx, """
            SELECT r.id,r.command_id,a.occurred_at,r.saleable_quantity,r.quarantine_quantity,r.restored_item_cost_minor
            FROM brickvault.stock_loss_recoveries r JOIN brickvault.audit a ON a.command_id=r.command_id
            WHERE r.loss_id=$1 AND ($2::integer IS NULL OR r.expected_purchase_revision<$2)
            ORDER BY r.expected_purchase_revision DESC LIMIT $3
            """, r => new StockLossRecoveryHistory(r.GetGuid(0),r.GetGuid(1),r.GetDateTime(2),r.GetInt32(3),r.GetInt32(4),
                Optional<int>(r,5),r.GetInt32(4)>0?r.GetGuid(0):null), token, postingId,
            Nullable(NpgsqlDbType.Integer,previous),historyLimit+1);
        Guid? nextHistory = null;
        if (history.Count > historyLimit)
        {
            history.RemoveAt(historyLimit);
            nextHistory = history[^1].Id;
        }
        return new StockLossRecoveryReview("stock-loss-recovery-review", original.AsOf, true, postingId, position.Revision,
            position.Value, original.Receipt, position.Shortage, original.Lost, original.Recovered, original.Lost-original.Recovered,
            original.Basis, original.Restored, original.Basis-original.Restored, original.Reason, original.At, sourceRows, next, history, nextHistory);
    }

    public async Task<StockLossRecoveryEvidence> Evidence(Guid locationId, Guid positionId, Guid postingId, Guid? recoveryId,
        Guid? commandId, string? after, int limit, CancellationToken token)
    {
        CheckLimit(limit);
        if (recoveryId.HasValue == commandId.HasValue)
            throw new Fault(400, "invalid_stock_loss_recovery_lookup", "Choose exactly one recovery receipt or command.");
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await BeginRead(connection, tx, token);
        var selector = recoveryId.HasValue ? "r.id=$4" : "r.command_id=$4";
        var result = (await Rows(connection, tx, """
            SELECT transaction_timestamp(),r.id,r.command_id,c.correlation_id,c.revision,r.purchase_id,
                loc.revision,loc.code,loc.name,r.location_code,r.recorded_catalog,r.reason,a.occurred_at,
                r.saleable_quantity,r.quarantine_quantity,r.restored_item_cost_minor,
                d.id,d.reference,d.quantity,d.item_cost_minor,p.currency,stock.origin_placement_id,stock.catalog_id,stock.catalog_revision
            FROM brickvault.stock_loss_recoveries r JOIN brickvault.commands c USING(command_id)
            JOIN brickvault.audit a USING(command_id)
            JOIN brickvault.stock_loss_postings l ON l.id=r.loss_id JOIN brickvault.deliveries d ON d.id=l.delivery_id
            JOIN brickvault.purchases p ON p.id=r.purchase_id JOIN brickvault.placement_positions stock ON stock.id=r.position_id
            JOIN brickvault.locations loc ON loc.id=r.location_id
            WHERE r.location_id=$1 AND r.position_id=$2 AND r.loss_id=$3 AND
            """ + " " + selector, r => new StockLossRecoveryEvidence("stock-loss-recovery",r.GetDateTime(0),true,r.GetGuid(1),
                r.GetGuid(2),r.GetGuid(3),r.GetInt32(4),r.GetGuid(5),positionId,postingId,
                new LocationRow(locationId,r.GetInt32(6),r.GetString(7),r.GetString(8)),r.GetString(9),
                JsonSerializer.Deserialize<CatalogRow>(r.GetString(10),Wire.Json)!,ReadReceipt(r,16),r.GetString(11),r.GetDateTime(12),
                r.GetInt32(13),r.GetInt32(14),Optional<int>(r,15),r.GetInt32(14)>0?r.GetGuid(1):null,[],null),
            token, locationId, positionId, postingId, (recoveryId ?? commandId)!.Value)).SingleOrDefault() ??
            throw new Fault(404, "unknown_stock_loss_recovery", "No matching recovery receipt belongs to this loss and position.");

        Guid? anchor = null;
        if (after is not null)
        {
            if (after.Length != 65 || after[32]!='.' || !Guid.TryParseExact(after.AsSpan(0,32),"N",out var scope) || scope!=result.Id ||
                !Guid.TryParseExact(after.AsSpan(33),"N",out var id)) throw InvalidCursor();
            await using var match = Query(connection, tx, """
                SELECT 1 FROM brickvault.stock_loss_recovery_allocations
                WHERE recovery_id=$1 AND quarantine_id IS NOT DISTINCT FROM nullif($2::uuid,'00000000-0000-0000-0000-000000000000')
                """, result.Id, id);
            if (await match.ExecuteScalarAsync(token) is null) throw InvalidCursor();
            anchor=id;
        }
        var allocations = await Rows(connection, tx, """
            SELECT quarantine_id,saleable_quantity,quarantine_quantity,restored_item_cost_minor
            FROM brickvault.stock_loss_recovery_allocations
            WHERE recovery_id=$1 AND ($2::uuid IS NULL OR quarantine_id>$2)
            ORDER BY quarantine_id NULLS FIRST LIMIT $3
            """, r => new StockLossRecoveryAllocation(Optional<Guid>(r,0),r.GetInt32(1),r.GetInt32(2),Optional<int>(r,3)),
            token,result.Id,Nullable(NpgsqlDbType.Uuid,anchor),limit+1);
        string? next = null;
        if (allocations.Count > limit)
        {
            allocations.RemoveAt(limit);
            next = $"{result.Id:N}.{allocations[^1].QuarantineId ?? Guid.Empty:N}";
        }
        return result with { Allocations=allocations,NextAfter=next };
    }

    private static AdjustmentReceiptEvidence ReadReceipt(NpgsqlDataReader r,int index) =>
        new(r.GetGuid(index),r.GetString(index+1),r.GetInt32(index+2),Optional<int>(r,index+3),r.GetString(index+4),
            r.GetGuid(index+5),r.GetGuid(index+6),r.GetInt32(index+7));
}
