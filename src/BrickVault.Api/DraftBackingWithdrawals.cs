using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record DraftBackingWithdrawalCommand
{
    public required Guid CommandId { get; init; }
    public required Guid PurchaseId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required Guid PositionId { get; init; }
    public required Guid OfferId { get; init; }
    public required Guid LocationId { get; init; }
    public required string ConfirmedLocationCode { get; init; }
    public required int Quantity { get; init; }
    public required string Reason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExpectedTotalOfferBackedQuantity { get; init; }

    public void Validate()
    {
        if (CommandId == Guid.Empty || PurchaseId == Guid.Empty || PositionId == Guid.Empty || OfferId == Guid.Empty ||
            LocationId == Guid.Empty || ExpectedRevision < 1 || Quantity is < 1 or > 1_000_000 ||
            ExpectedTotalOfferBackedQuantity is < 0 or > 64_000_000 || !TextFits(ConfirmedLocationCode, 48) || !TextFits(Reason, 200))
            throw new Fault(400, "invalid_draft_backing_withdrawal", "Choose the draft offer, receipt position and quantity, then confirm the bin and reason.");
    }

    private static bool TextFits(string? value, int limit) => value is { Length: > 0 } &&
        value.Length <= limit && value == value.Trim() && !value.Any(char.IsControl);

    public byte[] Digest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("offer-withdrawal\n"u8);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(this, Wire.Json));
        return hash.GetHashAndReset();
    }
}

internal sealed record DraftBackingOfferRow(Guid OfferId, int UnitPriceMinor, string Currency,
    int BackedQuantity, int TotalOfferBackedQuantity, string Authority, bool CanWithdraw,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PriceScaleVerified = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SetContents? SetContents = null)
{
    internal bool IsPooled { get; init; }
}

internal sealed record DraftBackingSnapshot(string Scope, DateTime AsOf, bool Complete, StockRegisterRow Position,
    int PurchaseRevision, bool HasOpenShortage, int PhysicalOnHand, int HeldQuantity, int SellableQuantity,
    int OfferBackedQuantity, int WithheldQuantity, int FreeToBackQuantity, IReadOnlyList<DraftBackingOfferRow> Offers,
    string? NextAfter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UnavailableQuantity = null);

internal sealed record DraftBackingContribution(Guid Id, Guid CommandId, Guid CorrelationId, int Revision,
    DateTime? RecordedAt, string Kind, int BackingDelta, Guid? RelatedPositionId, string? Reason);

internal sealed record DraftBackingHistory(string Scope, DateTime AsOf, bool Complete, StockRegisterRow Position,
    int PurchaseRevision, bool HasOpenShortage, int PhysicalOnHand, int HeldQuantity, int SellableQuantity,
    int OfferBackedQuantity, int WithheldQuantity, int FreeToBackQuantity, DraftBackingOfferRow Offer,
    IReadOnlyList<DraftBackingContribution> Contributions, string? NextAfter,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UnavailableQuantity = null);

internal sealed record DraftBackingWithdrawalEvidence(string Scope, DateTime AsOf, bool Complete, Guid Id, Guid CommandId,
    Guid CorrelationId, int Revision, Guid PurchaseId, Guid PositionId, Guid OfferId, LocationRow Location,
    string ConfirmedLocationCode, int Quantity, int PriorBackedQuantity, int RemainingBackedQuantity,
    int PriorTotalOfferBackedQuantity, int RemainingTotalOfferBackedQuantity, int UnitPriceMinor, string Currency,
    string Reason, DateTime RecordedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PriceScaleVerified = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SetContents? SetContents = null);

internal sealed class DraftBackingWithdrawals(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(DraftBackingWithdrawalCommand request, Guid correlationId, CancellationToken token)
    {
        request.Validate();
        var digest = request.Digest();
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (await CommandJournal.Replay(connection, tx, request.CommandId, digest, token) is { } replay) return replay;

        var id = Guid.NewGuid();
        try
        {
            await using var insert = Query(connection, tx, """
                INSERT INTO brickvault.draft_backing_withdrawals(id,command_id,purchase_id,position_id,offer_id,
                    location_id,location_code,expected_purchase_revision,quantity,reason,expected_offer_total)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                """, id, request.CommandId, request.PurchaseId, request.PositionId, request.OfferId, request.LocationId,
                request.ConfirmedLocationCode, request.ExpectedRevision, request.Quantity, request.Reason,
                Nullable(NpgsqlDbType.Integer, request.ExpectedTotalOfferBackedQuantity));
            await insert.ExecuteNonQueryAsync(token);
            var receipt = new Receipt(request.CommandId, id, checked(request.ExpectedRevision + 1), "offer-withdrawal", correlationId);
            await CommandJournal.Append(connection, tx, receipt, digest, "record-receiving", token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException ex) when (ex.ConstraintName is "draft_backing_revision_conflict" or "draft_backing_position_mismatch" or
            "draft_backing_bin_conflict" or "draft_backing_shortage_open" or "draft_backing_offer_mismatch" or
            "draft_backing_authority_unverified" or "draft_backing_quantity_unavailable" or "pooled_withdrawal_upgrade" or "draft_backing_total_conflict")
        {
            throw ex.ConstraintName switch
            {
                "draft_backing_revision_conflict" => Fault.Conflict(),
                "pooled_withdrawal_upgrade" => PooledOffers.Upgrade(),
                "draft_backing_total_conflict" => Conflict(ex.ConstraintName, "Backing from another position changed. Review the current offer total before confirming."),
                "draft_backing_shortage_open" => new Fault(409, "stock_shortage_open",
                    "Review backing in the open shortage workflow before changing it here."),
                "draft_backing_position_mismatch" => Conflict(ex.ConstraintName, "Choose a receipt position from this purchase and bin."),
                "draft_backing_bin_conflict" => Conflict(ex.ConstraintName, "Confirm the current registered bin code."),
                "draft_backing_offer_mismatch" => Conflict(ex.ConstraintName, "Choose an offer with a backing record at this position."),
                "draft_backing_authority_unverified" => Conflict(ex.ConstraintName, "The local journal does not verify this as an internal unpublished offer."),
                _ => Conflict(ex.ConstraintName, "Choose a quantity within the selected offer's current backing at this position.")
            };
        }
    }

    private sealed record PositionContext(DateTime AsOf, StockRegisterRow Position, int Revision, bool Open, int Held, int Unavailable)
    {
        internal BinStockLayer Layer => Position.Layer;
        internal int Sellable => Layer.Quantity - Held - Unavailable;
        internal int Withheld => Sellable - Layer.BackedQuantity;
    }

    private static Fault Conflict(string code, string message) => new(409, code, message);
    internal static Fault Upgrade() => new(426, "draft_backing_client_upgrade_required",
        "Update Workbench to read draft backing withdrawals in this history.");

    private static void CheckLimit(int limit)
    {
        if (limit is < 1 or > 100)
            throw new Fault(400, "invalid_draft_backing_page", "Choose a page size from 1 to 100.");
    }

    private static async Task BeginRead(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken token)
    {
        await using var mode = Query(connection, tx, "SET TRANSACTION READ ONLY");
        await mode.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection, tx, token);
    }

    private static async Task<PositionContext> ReadPosition(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid positionId, CancellationToken token, bool quarantine)
    {
        var rows = await Rows(connection, tx, "SELECT " + PositionRows.RegisterSelect + ",transaction_timestamp()\n" +
            PositionRows.RegisterFrom + "\nWHERE p.id=$1", r => new PositionContext(r.GetDateTime(40),
                PositionRows.ReadRegister(r, quarantine), r.GetInt32(29), r.GetBoolean(31), r.GetInt32(32), r.GetInt32(33)), token, positionId);
        var position = rows.SingleOrDefault() ?? throw new Fault(404, "unknown_position", "No stock position has that identifier.");
        if (!quarantine && position.Unavailable > 0) throw StockQuarantines.Upgrade();
        return position;
    }

    private static Task<List<DraftBackingOfferRow>> ReadOffers(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid positionId, Guid? offerId, Guid? after, int limit, bool open, CancellationToken token) =>
        Rows(connection, tx, """
            WITH candidates AS (
                SELECT DISTINCT b.offer_id FROM brickvault.offer_backing_ledger b
                WHERE b.placement_id=$1 AND ($2::uuid IS NULL OR b.offer_id=$2) AND ($3::uuid IS NULL OR b.offer_id>$3)
                ORDER BY b.offer_id LIMIT $4
            )
            SELECT o.id,o.unit_price_minor,o.currency,amounts.here,amounts.total,i.id IS NOT NULL,o.price_scale_verified,
                EXISTS (SELECT 1 FROM brickvault.offer_backing b WHERE b.offer_id=o.id AND b.purchase_id<>o.purchase_id),o.set_contents
            FROM candidates candidate JOIN brickvault.offers o ON o.id=candidate.offer_id
            LEFT JOIN brickvault.internal_draft_offers i ON i.id=o.id
            CROSS JOIN LATERAL (
                SELECT COALESCE(sum(b.quantity) FILTER (WHERE b.placement_id=$1),0)::integer AS here,
                    COALESCE(sum(b.quantity),0)::integer AS total
                FROM brickvault.current_offer_backing b WHERE b.offer_id=o.id
            ) amounts
            ORDER BY o.id
            """, r =>
            {
                var amount = r.GetInt32(3);
                var internalOnly = r.GetBoolean(5);
                return new DraftBackingOfferRow(r.GetGuid(0), r.GetInt32(1), r.GetString(2), amount, r.GetInt32(4),
                    internalOnly ? "internal-unpublished" : "unverified-origin", internalOnly && amount > 0 && !open,
                    OfferPriceScale.Verified(r.GetString(2), r.GetBoolean(6)), SetContents.Read(r,8)) { IsPooled = r.GetBoolean(7) };
            }, token, positionId, Nullable(NpgsqlDbType.Uuid, offerId), Nullable(NpgsqlDbType.Uuid, after), limit);

    public async Task<DraftBackingSnapshot> Snapshot(Guid positionId, string? after, int limit, CancellationToken token, bool quarantine = false, bool pooledOffers = false)
    {
        CheckLimit(limit);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await BeginRead(connection, tx, token);
        var p = await ReadPosition(connection, tx, positionId, token, quarantine);
        Guid? anchor = null;
        if (after is not null)
        {
            var parts = ScopeCursor(after, positionId, p.Revision, 3);
            if (!Guid.TryParseExact(parts[2], "N", out var offer)) throw InvalidCursor();
            await using var match = Query(connection, tx, """
                SELECT 1 FROM brickvault.offer_backing_ledger WHERE placement_id=$1 AND offer_id=$2 LIMIT 1
                """, positionId, offer);
            if (await match.ExecuteScalarAsync(token) is null) throw InvalidCursor();
            anchor = offer;
        }
        var rows = await ReadOffers(connection, tx, positionId, null, anchor, limit + 1, p.Open, token);
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = FormattableString.Invariant($"{positionId:N}.{p.Revision}.{rows[^1].OfferId:N}");
        }
        if (!pooledOffers && rows.Any(row => row.IsPooled)) throw PooledOffers.Upgrade();
        return new DraftBackingSnapshot("position-draft-backing", p.AsOf, true, p.Position, p.Revision, p.Open,
            p.Layer.Quantity, p.Held, p.Sellable, p.Layer.BackedQuantity, p.Withheld, p.Layer.FreeToBackQuantity, rows, next, quarantine ? p.Unavailable : null);
    }

    public async Task<DraftBackingHistory> History(Guid positionId, Guid offerId, string? after, int limit, CancellationToken token, bool quarantine = false, bool pooledOffers = false)
    {
        CheckLimit(limit);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await BeginRead(connection, tx, token);
        var p = await ReadPosition(connection, tx, positionId, token, quarantine);
        var offer = (await ReadOffers(connection, tx, positionId, offerId, null, 1, p.Open, token)).SingleOrDefault() ??
            throw new Fault(404, "unknown_draft_backing_offer", "No offer backing history belongs to this position and offer.");
        if (!pooledOffers && offer.IsPooled) throw PooledOffers.Upgrade();
        int? precedingRevision = null;
        Guid? precedingId = null;
        string? precedingKind = null;
        if (after is not null)
        {
            var parts = ScopeCursor(after, positionId, p.Revision, 5);
            if (!Guid.TryParseExact(parts[2], "N", out var cursorOffer) || cursorOffer != offerId ||
                !Guid.TryParseExact(parts[3], "N", out var id)) throw InvalidCursor();
            await using var match = Query(connection, tx, """
                SELECT COALESCE(origin.purchase_revision,c.revision) FROM brickvault.offer_backing_ledger b JOIN brickvault.commands c USING(command_id)
                LEFT JOIN brickvault.offer_backing origin ON b.kind='offer-backing' AND origin.offer_id=b.offer_id AND origin.placement_id=b.placement_id
                WHERE b.placement_id=$1 AND b.offer_id=$2 AND b.event_id=$3 AND b.kind=$4
                """, positionId, offerId, id, parts[4]);
            precedingRevision = (int?)await match.ExecuteScalarAsync(token) ?? throw InvalidCursor();
            precedingId = id;
            precedingKind = parts[4];
        }
        var rows = await Rows(connection, tx, """
            SELECT b.event_id,b.command_id,c.correlation_id,COALESCE(origin.purchase_revision,c.revision),a.occurred_at,b.kind,b.backing_delta,b.related_position_id,b.reason
            FROM brickvault.offer_backing_ledger b JOIN brickvault.commands c USING(command_id)
            LEFT JOIN brickvault.offer_backing origin ON b.kind='offer-backing' AND origin.offer_id=b.offer_id AND origin.placement_id=b.placement_id
            LEFT JOIN brickvault.audit a USING(command_id)
            WHERE b.placement_id=$1 AND b.offer_id=$2
                AND ($3::integer IS NULL OR (COALESCE(origin.purchase_revision,c.revision),b.kind COLLATE "C",b.event_id)<($3,$4::text COLLATE "C",$5::uuid))
            ORDER BY COALESCE(origin.purchase_revision,c.revision) DESC,b.kind COLLATE "C" DESC,b.event_id DESC LIMIT $6
            """, r => new DraftBackingContribution(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3),
                Optional<DateTime>(r, 4), r.GetString(5), r.GetInt32(6), Optional<Guid>(r, 7), r.IsDBNull(8) ? null : r.GetString(8)),
            token, positionId, offerId, Nullable(NpgsqlDbType.Integer, precedingRevision),
            Nullable(NpgsqlDbType.Text, precedingKind), Nullable(NpgsqlDbType.Uuid, precedingId), limit + 1);
        string? next = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(limit);
            next = FormattableString.Invariant($"{positionId:N}.{p.Revision}.{offerId:N}.{rows[^1].Id:N}.{rows[^1].Kind}");
        }
        return new DraftBackingHistory("position-draft-backing-history", p.AsOf, true, p.Position, p.Revision, p.Open,
            p.Layer.Quantity, p.Held, p.Sellable, p.Layer.BackedQuantity, p.Withheld, p.Layer.FreeToBackQuantity, offer, rows, next, quarantine ? p.Unavailable : null);
    }

    private static string[] ScopeCursor(string after, Guid positionId, int revision, int size)
    {
        if (after.Length > 128) throw InvalidCursor();
        var parts = after.Split('.');
        if (parts.Length != size || !Guid.TryParseExact(parts[0], "N", out var position) || position != positionId ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expected)) throw InvalidCursor();
        if (expected != revision)
            throw Conflict("stale_draft_backing_page", "The purchase changed. Reload the first page before continuing.");
        return parts;
    }

    private static Fault InvalidCursor() => new(400, "invalid_draft_backing_cursor",
        "Use the continuation from the selected position and offer history.");

    public async Task<DraftBackingWithdrawalEvidence> Evidence(Guid positionId, Guid? receiptId, Guid? commandId,
        CancellationToken token, bool pooledOffers = false)
    {
        if (receiptId.HasValue == commandId.HasValue)
            throw new Fault(400, "invalid_draft_backing_lookup", "Choose exactly one receipt or command identifier.");
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await BeginRead(connection, tx, token);
        var predicate = receiptId.HasValue ? "w.id=$2" : "w.command_id=$2";
        var rows = await Rows(connection, tx, """
            SELECT transaction_timestamp(),w.id,w.command_id,c.correlation_id,c.revision,w.purchase_id,w.offer_id,
                l.id,l.revision,l.code,l.name,w.location_code,w.quantity,w.prior_backed_quantity,w.prior_total_offer_backed_quantity,
                o.unit_price_minor,o.currency,w.reason,a.occurred_at,o.price_scale_verified,
                EXISTS (SELECT 1 FROM brickvault.offer_backing b WHERE b.offer_id=o.id AND b.purchase_id<>o.purchase_id),o.set_contents
            FROM brickvault.draft_backing_withdrawals w JOIN brickvault.commands c USING(command_id)
            JOIN brickvault.audit a USING(command_id) JOIN brickvault.offers o ON o.id=w.offer_id
            JOIN brickvault.locations l ON l.id=w.location_id
            WHERE w.position_id=$1 AND
            """ + " " + predicate, r =>
        {
            if (!pooledOffers && r.GetBoolean(20)) throw PooledOffers.Upgrade();
            var quantity = r.GetInt32(12);
            var prior = r.GetInt32(13);
            var total = r.GetInt32(14);
            return new DraftBackingWithdrawalEvidence("draft-backing-withdrawal", r.GetDateTime(0), true, r.GetGuid(1),
                r.GetGuid(2), r.GetGuid(3), r.GetInt32(4), r.GetGuid(5), positionId, r.GetGuid(6),
                new LocationRow(r.GetGuid(7), r.GetInt32(8), r.GetString(9), r.GetString(10)), r.GetString(11),
                quantity, prior, prior - quantity, total, total - quantity, r.GetInt32(15), r.GetString(16),
                r.GetString(17), r.GetDateTime(18), OfferPriceScale.Verified(r.GetString(16), r.GetBoolean(19)), SetContents.Read(r,21));
        }, token, positionId, (receiptId ?? commandId)!.Value);
        return rows.SingleOrDefault() ??
            throw new Fault(404, "unknown_draft_backing_withdrawal", "No withdrawal receipt matches this position and identifier.");
    }
}
