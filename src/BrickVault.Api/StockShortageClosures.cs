using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record StockShortageClosureCommand
{
    public required Guid CommandId { get; init; }
    public required Guid PurchaseId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required Guid PositionId { get; init; }
    public required Guid LocationId { get; init; }
    public required string ConfirmedLocationCode { get; init; }
    public required Guid CaseId { get; init; }
    public required Guid ExpectedObservationId { get; init; }
    public required string Reason { get; init; }

    public void Validate()
    {
        if (CommandId == Guid.Empty || PurchaseId == Guid.Empty || PositionId == Guid.Empty || LocationId == Guid.Empty ||
            CaseId == Guid.Empty || ExpectedObservationId == Guid.Empty || ExpectedRevision < 1 ||
            !TextFits(ConfirmedLocationCode, 48) || !TextFits(Reason, 200))
            throw new Fault(400, "invalid_shortage_closure", "Choose the open shortage and latest exact recount, then confirm the bin and reason.");
    }

    private static bool TextFits(string? value, int limit) => value is { Length: > 0 } &&
        value.Length <= limit && value == value.Trim() && !value.Any(char.IsControl);

    public byte[] Digest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("shortage-closure\n"u8);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(this, Wire.Json));
        return hash.GetHashAndReset();
    }
}

internal sealed record StockShortageClosureSummary(Guid Id, Guid CaseId, Guid PositionId, Guid CommandId,
    Guid OpeningObservationId, Guid RecountObservationId, DateTime RecordedAt);

internal sealed record StockShortageClosureEvidence(string Scope, DateTime AsOf, bool Complete, Guid Id, Guid CommandId,
    Guid CorrelationId, int Revision, Guid PurchaseId, Guid PositionId, Guid CaseId, LocationRow Location,
    string ConfirmedLocationCode, StockCountObservation OpeningObservation, StockCountObservation RecountObservation,
    AdjustmentReceiptEvidence Receipt, int LedgerQuantity, int QuarantinedQuantity, int HeldQuantity, int OfferBackedQuantity,
    string Reason, DateTime RecordedAt);

internal sealed class StockShortageClosures(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(StockShortageClosureCommand command, Guid correlationId, CancellationToken token)
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
            await using var insert = Query(connection, tx, """
                INSERT INTO brickvault.stock_shortage_closures(id,command_id,purchase_id,position_id,location_id,
                    location_code,case_id,observation_id,expected_purchase_revision,reason)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                """, id, command.CommandId, command.PurchaseId, command.PositionId, command.LocationId,
                command.ConfirmedLocationCode, command.CaseId, command.ExpectedObservationId, command.ExpectedRevision, command.Reason);
            await insert.ExecuteNonQueryAsync(token);
            var receipt = new Receipt(command.CommandId, id, checked(command.ExpectedRevision + 1), "shortage-closure", correlationId);
            await CommandJournal.Append(connection, tx, receipt, digest, "record-receiving", token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException ex) when (ex.ConstraintName is "shortage_closure_revision" or "shortage_closure_position_mismatch" or
            "shortage_closure_bin_conflict" or "shortage_closure_case_conflict" or "shortage_closure_observation_conflict" or
            "shortage_closure_count_not_exact" or "shortage_closure_commitments_inconsistent")
        {
            var code = ex.ConstraintName;
            throw code switch
            {
                "shortage_closure_revision" => Fault.Conflict(),
                "shortage_closure_position_mismatch" => new Fault(409, code, "Choose a current receipt position from this purchase and bin."),
                "shortage_closure_bin_conflict" => new Fault(409, code, "Confirm the current registered code for the affected bin."),
                "shortage_closure_case_conflict" => new Fault(409, code, "The selected shortage is no longer open at this position."),
                "shortage_closure_observation_conflict" => new Fault(409, code, "Reload the shortage and verify the newest recount against the current ledger quantity."),
                "shortage_closure_count_not_exact" => new Fault(409, code, "Only a later count exactly matching the current ledger can close a shortage without an adjustment."),
                _ => new Fault(409, code, "The current stock commitments cannot be reconciled with this recount.")
            };
        }
    }

    public async Task<StockShortageClosureEvidence> Read(Guid locationId, Guid positionId, Guid caseId, Guid? commandId,
        CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var mode = Query(connection, tx, "SET TRANSACTION READ ONLY"))
            await mode.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        var rows = await Rows(connection, tx, """
            SELECT transaction_timestamp(),s.id,s.command_id,c.correlation_id,c.revision,s.purchase_id,s.observation_id,
                l.revision,l.code,l.name,s.location_code,s.ledger_quantity,s.quarantined_quantity,s.held_quantity,
                s.offer_backed_quantity,s.reason,a.occurred_at,
                d.id,d.reference,d.quantity,d.item_cost_minor,p.currency,stock.origin_placement_id,stock.catalog_id,stock.catalog_revision
            FROM brickvault.stock_shortage_closures s
            JOIN brickvault.stock_shortage_cases closed ON closed.id=s.case_id AND closed.resolved_by_command_id=s.command_id
            JOIN brickvault.commands c ON c.command_id=s.command_id JOIN brickvault.audit a ON a.command_id=s.command_id
            JOIN brickvault.locations l ON l.id=s.location_id JOIN brickvault.purchases p ON p.id=s.purchase_id
            JOIN brickvault.current_placement_stock stock ON stock.id=s.position_id
            JOIN brickvault.deliveries d ON d.id=stock.delivery_id
            WHERE s.location_id=$1 AND s.position_id=$2 AND s.case_id=$3 AND ($4::uuid IS NULL OR s.command_id=$4)
            """, r => (AsOf:r.GetDateTime(0), Id:r.GetGuid(1), Command:r.GetGuid(2), Correlation:r.GetGuid(3), Revision:r.GetInt32(4),
                Purchase:r.GetGuid(5), Observation:r.GetGuid(6), Location:new LocationRow(locationId,r.GetInt32(7),r.GetString(8),r.GetString(9)),
                Code:r.GetString(10), Ledger:r.GetInt32(11), Quarantined:r.GetInt32(12), Held:r.GetInt32(13), Backed:r.GetInt32(14),
                Reason:r.GetString(15), At:r.GetDateTime(16), Receipt:new AdjustmentReceiptEvidence(r.GetGuid(17),r.GetString(18),
                    r.GetInt32(19),Optional<int>(r,20),r.GetString(21),r.GetGuid(22),r.GetGuid(23),r.GetInt32(24))),
            token, locationId, positionId, caseId, Nullable(NpgsqlDbType.Uuid, commandId));
        if (rows.Count == 0)
            throw new Fault(404, "unknown_shortage_closure", "No exact-recount closure matches this bin, position, shortage and command.");
        var row = rows.Single();
        var countRows = await Rows(connection, tx, StockCounts.ObservationProjection + " WHERE n.id=ANY($1::uuid[])",
            StockCounts.ReadObservation, token, new[] { caseId, row.Observation });
        var observations = countRows.ToDictionary(n => n.Id);
        return new StockShortageClosureEvidence("shortage-closure", row.AsOf, true, row.Id, row.Command, row.Correlation,
            row.Revision, row.Purchase, positionId, caseId, row.Location, row.Code, observations[caseId], observations[row.Observation],
            row.Receipt, row.Ledger, row.Quarantined, row.Held, row.Backed, row.Reason, row.At);
    }

    internal static Task<List<StockShortageClosureSummary>> ReadSummaries(NpgsqlConnection connection, NpgsqlTransaction tx,
        IReadOnlyList<StockCountObservation> observations, CancellationToken token) =>
        Rows(connection, tx, """
            SELECT DISTINCT s.id,s.case_id,s.position_id,s.command_id,s.observation_id,a.occurred_at
            FROM brickvault.stock_count_observations n
            JOIN brickvault.stock_shortage_closures s ON s.position_id=n.position_id
                AND n.expected_purchase_revision<s.expected_purchase_revision
            JOIN brickvault.stock_count_observations opening ON opening.id=s.case_id
            JOIN brickvault.stock_count_observations recount ON recount.id=s.observation_id
            JOIN brickvault.audit a ON a.command_id=s.command_id
            WHERE n.id=ANY($1::uuid[]) AND n.expected_purchase_revision BETWEEN
                opening.expected_purchase_revision AND recount.expected_purchase_revision
            ORDER BY a.occurred_at DESC,s.id
            """, r => new StockShortageClosureSummary(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),
                r.GetGuid(1),r.GetGuid(4),r.GetDateTime(5)), token, observations.Select(n => n.Id).ToArray());

    internal static Fault Upgrade() => new(426, "shortage_closure_client_upgrade_required",
        "Update Workbench to read exact-recount closures in this history.");
}
