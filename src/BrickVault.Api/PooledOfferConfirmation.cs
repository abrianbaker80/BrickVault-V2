using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal static class PooledOfferConfirmation
{
    internal const string Unapplied = "pooled-offer-unapplied";

    // The browser can prove the complete original command even after a gateway
    // refuses to forward a large retry. The tuple preserves ordered source rows.
    internal static byte[] Digest(PooledOfferCommand offer)
    {
        var sources = offer.Sources.Select(source => new object[] {
            source.PurchaseId, source.ExpectedRevision,
            source.Backing.Select(row => new object[] { row.PlacementId, row.Quantity }).ToArray()
        }).ToArray();
        var contents = offer.SetContents is {} set ? new[] { set.IncludesBox, set.IncludesInstructions } : null;
        object?[] fields = ["brickvault.pooled-offer.v1", offer.CommandId, offer.CatalogId,
            offer.ExpectedCatalogRevision, offer.Currency, offer.UnitPriceMinor, contents, sources];
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields));
    }

    internal static async Task<IResult> Settle(NpgsqlDataSource db, Guid commandId, HttpContext context, CancellationToken token)
    {
        ((Session)context.Items[typeof(Session)]!).RequireWorkspace(context.Request.Headers);
        var value = context.Request.Headers["X-BrickVault-Command-Digest"];
        if (value.Count != 1 || value[0] is not { Length: 64 } hex || !hex.All(char.IsAsciiHexDigit))
            throw new Fault(400, "invalid_command_digest", "Supply the complete SHA-256 digest of the original pooled offer.");

        if (commandId == Guid.Empty) throw new Fault(400, "invalid_command_id", "Choose the original command ID.");
        var digest = Convert.FromHexString(hex);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        var receipt = await CommandJournal.Replay(connection, tx, commandId, digest, token, allowUnapplied: true);
        if (receipt is null)
        {
            // An earlier POST might still be in transit. Claim its original ID before
            // the operator is allowed to submit a replacement. Either outcome is final.
            var closed = new Receipt(commandId, commandId, 1, Unapplied, (Guid)context.Items[typeof(Guid)]!);
            await CommandJournal.Append(connection, tx, closed, digest, "record-receiving", token);
            await tx.CommitAsync(token);
            return Closed(context, digest);
        }
        if (receipt.Kind == Unapplied) return Closed(context, digest);
        if (receipt is { Kind: "offer" })
        {
            await using var find = Query(connection, tx, """
                SELECT EXISTS (SELECT 1 FROM brickvault.offers o JOIN brickvault.offer_backing b ON b.offer_id=o.id
                    WHERE o.id=$1 AND b.purchase_id<>o.purchase_id)
                """, receipt.EntityId);
            if (await find.ExecuteScalarAsync(token) is true) return Results.Ok(receipt);
        }
        throw new Fault(409, "command_mismatch", "This command does not identify a pooled offer.");
    }

    private static IResult Closed(HttpContext context, byte[] digest)
    {
        context.Response.Headers["X-BrickVault-Settled-Digest"] = Convert.ToHexStringLower(digest);
        return Results.NoContent();
    }
}
