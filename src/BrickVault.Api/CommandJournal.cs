using System.Security.Cryptography;
using Npgsql;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal static class CommandJournal
{
    public static async Task<Receipt?> Replay(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid commandId, byte[] digest, CancellationToken token, bool allowUnapplied = false)
    {
        // The same command ID serializes across endpoints and API instances before the first write.
        await using var guard = Query(connection, tx,
            "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 44318))", commandId);
        await guard.ExecuteNonQueryAsync(token);
        await using var find = Query(connection, tx, """
            SELECT digest,entity_id,revision,kind,correlation_id FROM brickvault.commands WHERE command_id=$1
            """, commandId);
        await using var row = await find.ExecuteReaderAsync(token);
        if (!await row.ReadAsync(token)) return null;
        if (!CryptographicOperations.FixedTimeEquals(digest, row.GetFieldValue<byte[]>(0)))
            throw new Fault(409, "command_mismatch", "This command ID already identifies a different request.");
        if (!allowUnapplied && row.GetString(3) == PooledOfferConfirmation.Unapplied)
            throw new Fault(409, "pooled_offer_unapplied", "This request was closed without creating an offer.");
        return new Receipt(commandId, row.GetGuid(1), row.GetInt32(2), row.GetString(3), row.GetGuid(4));
    }

    public static async Task Append(NpgsqlConnection connection, NpgsqlTransaction tx, Receipt receipt,
        byte[] digest, string jobKind, CancellationToken token)
    {
        await using (var command = Query(connection, tx, """
            INSERT INTO brickvault.commands(command_id,kind,digest,entity_id,revision,correlation_id)
            VALUES ($1,$2,$3,$4,$5,$6)
            """, receipt.CommandId, receipt.Kind, digest, receipt.EntityId, receipt.Revision, receipt.CorrelationId))
            await command.ExecuteNonQueryAsync(token);
        await using (var audit = Query(connection, tx,
            "INSERT INTO brickvault.audit(command_id,actor) VALUES ($1,'operator')", receipt.CommandId))
            await audit.ExecuteNonQueryAsync(token);
        await using (var job = Query(connection, tx, """
            INSERT INTO brickvault.jobs(command_id,kind,payload_version) VALUES ($1,$2,1)
            """, receipt.CommandId, jobKind))
            await job.ExecuteNonQueryAsync(token);
    }
}
