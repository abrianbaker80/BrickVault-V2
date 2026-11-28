using System.Data;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class StockLosses(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(StockLossCommand command, Guid correlationId, CancellationToken token)
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
            await using (var posting = Query(connection, tx, """
                INSERT INTO brickvault.stock_loss_postings(id,command_id,purchase_id,position_id,location_id,location_code,
                    case_id,observation_id,expected_purchase_revision,reason)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                """, id, command.CommandId, command.PurchaseId, command.PositionId, command.LocationId,
                command.ConfirmedLocationCode, command.CaseId, command.ExpectedObservationId, command.ExpectedRevision,
                command.Reason))
                await posting.ExecuteNonQueryAsync(token);

            if (command.QuarantineAttributions is { } selected)
            {
                await using var review = Query(connection, tx,
                    "INSERT INTO brickvault.stock_loss_quarantine_reviews(loss_id) VALUES ($1)", id);
                await review.ExecuteNonQueryAsync(token);
                if (selected.Length > 0)
                {
                    await using var attribution = Query(connection, tx, """
                        INSERT INTO brickvault.stock_loss_quarantine_attributions(loss_id,quarantine_id,quantity)
                        SELECT $1,quarantine_id,quantity FROM unnest($2::uuid[],$3::integer[]) AS chosen(quarantine_id,quantity)
                        """, id, selected.Select(line => line.QuarantineId).ToArray(), selected.Select(line => line.Quantity).ToArray());
                    await attribution.ExecuteNonQueryAsync(token);
                }
            }

            if (command.OfferReductions.Length != 0)
            {
                await using var reductions = Query(connection, tx, """
                    INSERT INTO brickvault.stock_loss_offer_reductions(loss_id,offer_id,quantity)
                    SELECT $1,offer_id,quantity FROM unnest($2::uuid[],$3::integer[]) AS selected(offer_id,quantity)
                    """, id, command.OfferReductions.Select(x => x.OfferId).ToArray(),
                    command.OfferReductions.Select(x => x.Quantity).ToArray());
                await reductions.ExecuteNonQueryAsync(token);
            }

            var receipt = new Receipt(command.CommandId, id, checked(command.ExpectedRevision + 1), "stock-loss", correlationId);
            await CommandJournal.Append(connection, tx, receipt, digest, "record-receiving", token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException ex) when (ex.ConstraintName is
            "stock_loss_revision" or "stock_loss_position_mismatch" or "stock_loss_bin_confirmation_mismatch" or
            "stock_loss_case_conflict" or "stock_loss_observation_conflict" or "stock_loss_no_loss" or
            "stock_loss_protected_shortfall" or "stock_loss_offer_reduction_mismatch" or "stock_loss_quarantine_unresolved" or
            "stock_loss_offer_reductions_offer_id_fkey" or "stock_loss_quarantine_attribution_mismatch")
        {
            throw ex.ConstraintName switch
            {
                "stock_loss_revision" => Fault.Conflict(),
                "stock_loss_position_mismatch" => Conflict(ex.ConstraintName, "Choose a current receipt position from this purchase and bin."),
                "stock_loss_bin_confirmation_mismatch" => Conflict(ex.ConstraintName, "Confirm the current registered code for the affected bin."),
                "stock_loss_case_conflict" => Conflict(ex.ConstraintName, "This shortage is no longer open for the selected position."),
                "stock_loss_observation_conflict" => Conflict(ex.ConstraintName, "Reload the shortage and review the latest witness at the current ledger quantity."),
                "stock_loss_no_loss" => Conflict(ex.ConstraintName, "The latest witnessed quantity does not support posting a loss."),
                "stock_loss_protected_shortfall" => Conflict(ex.ConstraintName, "The counted units outside remaining quarantine cannot cover the protected holds. Review the attribution and those holds before posting."),
                "stock_loss_quarantine_unresolved" => Conflict(ex.ConstraintName, "Confirm zero quarantined units missing or identify the missing quantity on each affected quarantine receipt."),
                "stock_loss_quarantine_attribution_mismatch" => Conflict(ex.ConstraintName,
                    "Attribute only witnessed missing units on active quarantine receipts at this position. Reconcile the ordinary and quarantined quantities."),
                _ => Conflict("stock_loss_offer_reduction_mismatch", "Choose exact reductions for offers backed at this position up to the required total.")
            };
        }
    }

    private static Fault Conflict(string code, string message) => new(409, code, message);

    public async Task<StockLossSnapshot> Read(Guid locationId, Guid positionId, Guid? caseId, Guid? observationId,
        Guid? postingId, Guid? commandId, string? after, int limit, CancellationToken token,
        bool quarantine = false, string? quarantineAfter = null, int quarantineLimit = 25, bool recoveries = false, bool partOuts = false)
    {
        var selectors = new[] { caseId, observationId, postingId, commandId }.Count(id => id.HasValue);
        if (selectors != 1 || limit is < 1 or > 100 || quarantineLimit is < 1 or > 100 ||
            (!quarantine && quarantineAfter is not null))
            throw new Fault(400, "invalid_stock_loss_lookup",
                "Select exactly one case, observation, posting or command and use page sizes from 1 to 100.");

        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var mode = Query(connection, tx, "SET TRANSACTION READ ONLY"))
            await mode.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection, tx, token);

        var selector = caseId ?? observationId ?? postingId ?? commandId!.Value;
        var predicate = observationId.HasValue ? """
            AND EXISTS (
                SELECT 1 FROM brickvault.stock_count_observations requested
                JOIN brickvault.stock_count_observations opener ON opener.id=l.case_id
                JOIN brickvault.stock_count_observations reviewed ON reviewed.id=l.observation_id
                WHERE requested.id=$3 AND requested.position_id=l.position_id
                    AND requested.expected_purchase_revision BETWEEN opener.expected_purchase_revision
                        AND reviewed.expected_purchase_revision
            )
            """ : caseId.HasValue ? "AND l.case_id=$3" : postingId.HasValue ? "AND l.id=$3" : "AND l.command_id=$3";
        var postings = await Rows(connection, tx, """
            SELECT transaction_timestamp(),loc.revision,loc.code,loc.name,l.id,l.case_id,l.purchase_id,
                l.command_id,c.correlation_id,c.revision,a.occurred_at,l.observation_id,l.location_code,l.reason,
                l.ledger_quantity_before,l.lost_quantity,l.protected_project_quantity,l.offer_backed_quantity_before,
                l.prior_delivery_loss_quantity,l.allocated_item_cost_minor,l.remaining_delivery_item_cost_minor,
                d.id,d.reference,d.quantity,d.item_cost_minor,buy.currency,p.origin_placement_id,p.catalog_id,p.catalog_revision,
                COALESCE((SELECT sum(r.quantity) FROM brickvault.stock_loss_offer_reductions r WHERE r.loss_id=l.id),0)::integer,
                q.quarantined_quantity_before,
                COALESCE((SELECT sum(qa.quantity) FROM brickvault.stock_loss_quarantine_attributions qa WHERE qa.loss_id=l.id),0)::integer,
                COALESCE(basis.had_prior_recovery,false),context.prior_transformed_quantity,context.prior_transformed_item_cost_minor,
                EXISTS (SELECT 1 FROM brickvault.stock_part_out_outputs o WHERE o.delivery_id=l.delivery_id)
            FROM brickvault.stock_loss_postings l JOIN brickvault.commands c ON c.command_id=l.command_id
            LEFT JOIN brickvault.stock_loss_part_out_context context ON context.loss_id=l.id
            LEFT JOIN brickvault.stock_loss_basis_assignments basis ON basis.loss_id=l.id
            LEFT JOIN brickvault.stock_loss_quarantine_reviews q ON q.loss_id=l.id
            JOIN brickvault.audit a ON a.command_id=l.command_id
            JOIN brickvault.locations loc ON loc.id=l.location_id
            JOIN brickvault.deliveries d ON d.id=l.delivery_id
            JOIN brickvault.purchases buy ON buy.id=l.purchase_id
            JOIN brickvault.placement_positions p ON p.id=l.position_id
            WHERE l.location_id=$1 AND l.position_id=$2
            """ + "\n" + predicate, r => (AsOf: r.GetDateTime(0), Location: new LocationRow(locationId, r.GetInt32(1),
                r.GetString(2), r.GetString(3)), Id: r.GetGuid(4), Case: r.GetGuid(5), Purchase: r.GetGuid(6),
                Command: r.GetGuid(7), Correlation: r.GetGuid(8), Revision: r.GetInt32(9), At: r.GetDateTime(10),
                Observation: r.GetGuid(11), LocationCode: r.GetString(12), Reason: r.GetString(13), LedgerBefore: r.GetInt32(14),
                Loss: r.GetInt32(15), Protected: r.GetInt32(16), BackedBefore: r.GetInt32(17), PriorLoss: r.GetInt32(18),
                AllocatedCost: Optional<int>(r,19), RemainingCost: Optional<int>(r,20),
                Receipt: new AdjustmentReceiptEvidence(r.GetGuid(21), r.GetString(22), r.GetInt32(23), Optional<int>(r,24),
                    r.GetString(25), r.GetGuid(26), r.GetGuid(27), r.GetInt32(28)), Reduction: r.GetInt32(29), QuarantineBefore: Optional<int>(r,30), QuarantineLoss: r.GetInt32(31), PriorRecovery: r.GetBoolean(32), Transformed: Optional<int>(r,33),
                TransformedBasis: Optional<int>(r,34), IsDerived: r.GetBoolean(35)),
            token, locationId, positionId, selector);
        if (postings.Count == 0)
            throw new Fault(404, "unknown_stock_loss", "No matching posted stock loss belongs to this receipt position and bin.");
        var row = postings.Single();
        if (!quarantine && row.QuarantineBefore > 0) throw StockQuarantines.Upgrade();
        if (!recoveries && row.PriorRecovery) throw StockLossRecoveries.Upgrade();
        if (!partOuts && (row.Transformed.HasValue || row.IsDerived)) throw StockPartOuts.Upgrade();
        var reviewed = (await Rows(connection, tx, StockCounts.ObservationProjection + " WHERE n.id=$1",
            StockCounts.ReadObservation, token, row.Observation)).Single();

        var cursor = await CheckCursor(connection, tx, row.Id, after, token);
        var reductions = await Rows(connection, tx, """
            SELECT r.offer_id,r.quantity,r.prior_backed_quantity,o.unit_price_minor,o.currency,o.price_scale_verified
            FROM brickvault.stock_loss_offer_reductions r JOIN brickvault.offers o ON o.id=r.offer_id
            WHERE r.loss_id=$1 AND ($2::uuid IS NULL OR r.offer_id>$2) ORDER BY r.offer_id LIMIT $3
            """, r => new StockLossReduction(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(2)-r.GetInt32(1),
                r.GetInt32(3), r.GetString(4), OfferPriceScale.Verified(r.GetString(4), r.GetBoolean(5))), token, row.Id, Nullable(NpgsqlDbType.Uuid, cursor), limit + 1);
        string? next = null;
        if (reductions.Count > limit)
        {
            reductions.RemoveAt(limit);
            next = $"{row.Id:N}.{reductions[^1].OfferId:N}";
        }
        IReadOnlyList<StockLossQuarantineAttribution>? attributions = null;
        string? nextQuarantine = null;
        if (quarantine)
        {
            Guid? attributionAnchor = null;
            if (quarantineAfter is not null)
            {
                if (!TryScopedCursor(quarantineAfter, row.Id, out var anchorId)) throw InvalidQuarantineCursor();
                await using var anchor = Query(connection, tx, """
                    SELECT 1 FROM brickvault.stock_loss_quarantine_attributions WHERE loss_id=$1 AND quarantine_id=$2
                    """, row.Id, anchorId);
                if (await anchor.ExecuteScalarAsync(token) is null) throw InvalidQuarantineCursor();
                attributionAnchor = anchorId;
            }
            var page = await Rows(connection, tx, """
                SELECT quarantine_id,quantity,prior_remaining_quantity
                FROM brickvault.stock_loss_quarantine_attributions
                WHERE loss_id=$1 AND ($2::uuid IS NULL OR quarantine_id>$2)
                ORDER BY quarantine_id LIMIT $3
                """, r => new StockLossQuarantineAttribution(r.GetGuid(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(2)-r.GetInt32(1)),
                token, row.Id, Nullable(NpgsqlDbType.Uuid, attributionAnchor), quarantineLimit + 1);
            if (page.Count > quarantineLimit)
            {
                page.RemoveAt(quarantineLimit);
                nextQuarantine = $"{row.Id:N}.{page[^1].QuarantineId:N}";
            }
            attributions = page;
        }
        return new StockLossSnapshot("stock-loss-posting", row.AsOf, true, row.Location, positionId, row.Case,
            row.Purchase, row.Id, row.Command, row.Correlation, row.Revision, reviewed, row.Receipt, row.LocationCode,
            row.Reason, row.At, row.LedgerBefore, row.LedgerBefore-row.Loss, row.Loss, row.Protected, row.BackedBefore,
            row.BackedBefore-row.Reduction, row.PriorLoss, row.AllocatedCost, row.RemainingCost, reductions, next,
            quarantine ? row.QuarantineBefore.HasValue : null, quarantine ? row.QuarantineBefore ?? 0 : null,
            quarantine ? (row.QuarantineBefore ?? 0)-row.QuarantineLoss : null, attributions, nextQuarantine,
            recoveries ? row.PriorRecovery ? "active" : "cumulative" : null,
            partOuts ? row.Transformed : null,partOuts ? row.TransformedBasis : null);
    }

    private static async Task<Guid?> CheckCursor(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid id, string? after, CancellationToken token)
    {
        if (after is null) return null;
        if (!TryScopedCursor(after, id, out var offer)) throw InvalidCursor();
        await using var match = Query(connection, tx, """
            SELECT 1 FROM brickvault.stock_loss_offer_reductions WHERE loss_id=$1 AND offer_id=$2
            """, id, offer);
        if (await match.ExecuteScalarAsync(token) is null) throw InvalidCursor();
        return offer;
    }

    private static bool TryScopedCursor(string cursor, Guid id, out Guid anchor)
    {
        anchor = default;
        return cursor.Length == 65 && cursor[32] == '.' && Guid.TryParseExact(cursor.AsSpan(0,32), "N", out var posting) &&
            posting == id && Guid.TryParseExact(cursor.AsSpan(33), "N", out anchor);
    }

    private static Fault InvalidQuarantineCursor() =>
        new(400, "invalid_stock_loss_quarantine_cursor", "Use the next attribution cursor from this posted loss.");

    private static Fault InvalidCursor() =>
        new(400, "invalid_stock_loss_cursor", "Use the next reduction cursor from this posted stock loss.");
}
