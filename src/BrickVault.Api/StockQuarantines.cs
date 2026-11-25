using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal record StockQuarantineCommand
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
    public required string Reason { get; init; }

    [JsonIgnore]
    public string Kind => this is StockQuarantineReleaseCommand ? "quarantine-release" : "quarantine";

    public void Validate()
    {
        if (CommandId == Guid.Empty || PurchaseId == Guid.Empty || PositionId == Guid.Empty ||
            LocationId == Guid.Empty || CatalogId == Guid.Empty || ExpectedRevision < 1 || CatalogRevision < 1 ||
            Quantity is < 1 or > 1_000_000 || !TextFits(ConfirmedLocationCode, 48) || !TextFits(Reason, 200) ||
            (this is StockQuarantineReleaseCommand release &&
                (release.QuarantineId == Guid.Empty || release.InspectionResult != "passed")))
            throw new Fault(400, "invalid_quarantine", "Confirm the registered bin, catalog revision, quantity and reason. A release needs a receipt and a passed inspection.");
    }

    private static bool TextFits(string? value, int limit) => value is { Length: > 0 } &&
        value.Length <= limit && value == value.Trim() && !value.Any(char.IsControl);

    public byte[] Digest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Kind + "\n"));
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(this, GetType(), Wire.Json));
        return hash.GetHashAndReset();
    }
}

internal sealed record StockQuarantineReleaseCommand : StockQuarantineCommand
{
    public required Guid QuarantineId { get; init; }
    public required string InspectionResult { get; init; }
}

internal sealed record StockQuarantineRow(Guid Id, int Quantity, int ReleasedQuantity, int RemainingQuantity,
    string Reason, Guid CommandId, Guid CorrelationId, DateTime RecordedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LostQuantity = null);

internal sealed record StockQuarantineSnapshot(string Scope, DateTime AsOf, bool Complete, StockRegisterRow Position,
    int PurchaseRevision, bool HasOpenShortage, int OnHandQuantity, int QuarantinedQuantity, int HeldQuantity,
    int OfferBackedQuantity, int SellableQuantity, int AvailableToQuarantineQuantity,
    IReadOnlyList<StockQuarantineRow> Quarantines, Guid? NextAfter);

internal sealed record StockQuarantineEvidence(string Scope, DateTime AsOf, bool Complete, Guid PositionId,
    LocationRow Location, string ConfirmedLocationCode, Guid CatalogId, int CatalogRevision, CatalogRow RecordedCatalog,
    Guid DeliveryId, Guid PurchaseId,
    string Kind, Guid Id, Guid CommandId, Guid CorrelationId, int Revision, Guid QuarantineId, string? InspectionResult,
    int Quantity, string Reason, DateTime RecordedAt);

internal sealed class StockQuarantines(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(StockQuarantineCommand request, Guid correlationId, CancellationToken token)
    {
        request.Validate();
        var digest = request.Digest();
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (await CommandJournal.Replay(connection, tx, request.CommandId, digest, token) is { } replay) return replay;

        var id = Guid.NewGuid();
        object[] fields = [id,request.CommandId,request.PurchaseId,request.PositionId,request.LocationId,
            request.ConfirmedLocationCode,request.CatalogId,request.CatalogRevision,request.ExpectedRevision,request.Quantity,
            request.Reason];
        try
        {
            await using var insert = request is StockQuarantineReleaseCommand release
                ? Query(connection, tx, """
                    INSERT INTO brickvault.stock_quarantine_releases(id,command_id,purchase_id,position_id,location_id,
                        location_code,catalog_id,catalog_revision,expected_purchase_revision,quantity,reason,quarantine_id,inspection_result)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
                    """, [.. fields, release.QuarantineId, release.InspectionResult])
                : Query(connection, tx, """
                    INSERT INTO brickvault.stock_quarantines(id,command_id,purchase_id,position_id,location_id,
                        location_code,catalog_id,catalog_revision,expected_purchase_revision,quantity,reason)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                    """, fields);
            await insert.ExecuteNonQueryAsync(token);
            var receipt = new Receipt(request.CommandId, id, checked(request.ExpectedRevision + 1), request.Kind, correlationId);
            await CommandJournal.Append(connection, tx, receipt, digest, "record-receiving", token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException ex) when (ex.ConstraintName is "quarantine_revision_conflict" or "quarantine_position_mismatch" or
            "quarantine_bin_conflict" or "quarantine_catalog_conflict" or "quarantine_shortage_open" or
            "quarantine_quantity_unavailable" or "unknown_quarantine_receipt")
        {
            throw ex.ConstraintName switch
            {
                "quarantine_revision_conflict" => Fault.Conflict(),
                "quarantine_position_mismatch" => Conflict(ex.ConstraintName, "Choose a current position from this purchase and bin."),
                "quarantine_bin_conflict" => Conflict(ex.ConstraintName, "Confirm the current code for the registered bin."),
                "quarantine_catalog_conflict" => Conflict(ex.ConstraintName, "Reload the position and verify the current catalog identity and revision. Release requires eligible identification."),
                "quarantine_shortage_open" => Conflict(ex.ConstraintName, "An open shortage must be reviewed before changing quarantined units."),
                "unknown_quarantine_receipt" => Conflict(ex.ConstraintName, "Choose a quarantine receipt at this position."),
                _ => Conflict(ex.ConstraintName, "Choose a quantity within the uncommitted stock or selected quarantine receipt. Existing commitments are protected.")
            };
        }
    }

    public static Fault ProtectedCapacity() =>
        Conflict("quarantine_capacity_unavailable", "Quarantined units cannot back an offer, be reserved for a build or be transferred.");

    public static Fault Upgrade() =>
        new(426, "quarantine_client_upgrade_required", "Update Workbench to read quarantined stock and its recorded history.");

    private static Fault Conflict(string code, string message) => new(409, code, message);

    public async Task<StockQuarantineSnapshot> Snapshot(Guid positionId, Guid? after, int limit, CancellationToken token, bool lossAttribution = false)
    {
        if (limit is < 1 or > 100)
            throw new Fault(400, "invalid_quarantine_page", "Choose a quarantine page size from 1 to 100.");
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var mode = Query(connection, tx, "SET TRANSACTION READ ONLY"))
            await mode.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        var position = (await Rows(connection, tx, "SELECT " + PositionRows.RegisterSelect + "\n" +
            PositionRows.RegisterFrom + "\nWHERE p.id=$1", r => (Row: PositionRows.ReadRegister(r, true),
                Revision: r.GetInt32(29), Open: r.GetBoolean(31), Held: r.GetInt32(32)), token, positionId)).SingleOrDefault();
        if (position.Row is null) throw new Fault(404, "unknown_position", "No stock position has that identifier.");
        if (!lossAttribution)
        {
            await using var consumed = Query(connection, tx, """
                SELECT EXISTS (
                    SELECT 1 FROM brickvault.stock_quarantines q
                    JOIN brickvault.stock_loss_quarantine_attributions a ON a.quarantine_id=q.id
                    WHERE q.position_id=$1
                )
                """, positionId);
            if ((bool)(await consumed.ExecuteScalarAsync(token))!) throw Upgrade();
        }

        int? previous = null;
        if (after.HasValue)
        {
            await using var cursor = Query(connection, tx, """
                SELECT expected_purchase_revision FROM brickvault.stock_quarantines WHERE id=$1 AND position_id=$2
                """, after.Value, positionId);
            previous = (int?)await cursor.ExecuteScalarAsync(token);
            if (previous is null)
                throw new Fault(400, "invalid_quarantine_cursor", "Use a continuation from this position's quarantine history.");
        }
        var rows = await Rows(connection, tx, """
            SELECT q.id,q.quantity,q.released_quantity,q.remaining_quantity,q.reason,q.command_id,c.correlation_id,a.occurred_at,q.lost_quantity
            FROM brickvault.current_stock_quarantines q JOIN brickvault.commands c USING(command_id)
            JOIN brickvault.audit a ON a.command_id=q.command_id
            WHERE q.position_id=$1 AND ($2::integer IS NULL OR q.expected_purchase_revision<$2)
            ORDER BY q.expected_purchase_revision DESC LIMIT $3
            """, r => new StockQuarantineRow(r.GetGuid(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.GetString(4),
                r.GetGuid(5),r.GetGuid(6),r.GetDateTime(7),lossAttribution ? r.GetInt32(8) : null),
            token, positionId, Nullable(NpgsqlDbType.Integer, previous), limit + 1);
        Guid? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = rows[^1].Id;
        }
        await using var clock = Query(connection, tx, "SELECT transaction_timestamp()");
        var layer = position.Row.Layer;
        var quarantined = layer.QuarantinedQuantity.GetValueOrDefault();
        var sellable = layer.Quantity - quarantined - position.Held;
        return new StockQuarantineSnapshot("position-quarantine", (DateTime)(await clock.ExecuteScalarAsync(token))!, true,
            position.Row, position.Revision, position.Open, layer.Quantity, quarantined, position.Held, layer.BackedQuantity,
            sellable, position.Open ? 0 : sellable - layer.BackedQuantity, rows, next);
    }

    public async Task<StockQuarantineEvidence> Evidence(Guid positionId, Guid? receiptId, Guid? commandId, CancellationToken token)
    {
        if (receiptId.HasValue == commandId.HasValue)
            throw new Fault(400, "invalid_quarantine_lookup", "Select exactly one quarantine receipt or command.");
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await Migration.CheckWithin(connection, tx, token);
        return (await Rows(connection, tx, """
            WITH journal AS (
                SELECT q.id,q.command_id,q.purchase_id,q.position_id,q.location_id,q.location_code,q.catalog_id,q.catalog_revision,
                    q.quantity,q.reason,q.id AS quarantine_id,NULL::text AS inspection_result,q.recorded_catalog
                FROM brickvault.stock_quarantines q WHERE q.position_id=$1
                UNION ALL
                SELECT r.id,r.command_id,r.purchase_id,r.position_id,r.location_id,r.location_code,r.catalog_id,r.catalog_revision,
                    r.quantity,r.reason,r.quarantine_id,r.inspection_result,r.recorded_catalog
                FROM brickvault.stock_quarantine_releases r WHERE r.position_id=$1
            )
            SELECT transaction_timestamp(),j.location_id,l.revision,l.code,l.name,j.location_code,j.catalog_id,j.catalog_revision,
                p.delivery_id,j.purchase_id,c.kind,j.id,j.command_id,c.correlation_id,c.revision,j.quarantine_id,
                j.inspection_result,j.quantity,j.reason,a.occurred_at,j.recorded_catalog
            FROM journal j JOIN brickvault.commands c USING(command_id) JOIN brickvault.audit a ON a.command_id=j.command_id
            JOIN brickvault.locations l ON l.id=j.location_id JOIN brickvault.current_placement_stock p ON p.id=j.position_id
            WHERE ($2::uuid IS NULL OR j.id=$2) AND ($3::uuid IS NULL OR j.command_id=$3)
            """, r => new StockQuarantineEvidence("quarantine-receipt",r.GetDateTime(0),true,positionId,
                new LocationRow(r.GetGuid(1),r.GetInt32(2),r.GetString(3),r.GetString(4)),r.GetString(5),r.GetGuid(6),r.GetInt32(7),
                JsonSerializer.Deserialize<CatalogRow>(r.GetString(20), Wire.Json)!, r.GetGuid(8),r.GetGuid(9),r.GetString(10),r.GetGuid(11),r.GetGuid(12),r.GetGuid(13),r.GetInt32(14),r.GetGuid(15),
                r.IsDBNull(16) ? null : r.GetString(16),r.GetInt32(17),r.GetString(18),r.GetDateTime(19)), token, positionId,
            Nullable(NpgsqlDbType.Uuid, receiptId), Nullable(NpgsqlDbType.Uuid, commandId))).SingleOrDefault() ??
            throw new Fault(404, "unknown_quarantine_receipt", "No matching quarantine receipt belongs to this position.");
    }
}
