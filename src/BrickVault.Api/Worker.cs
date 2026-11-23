using Npgsql;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal static class Worker
{
    public static async Task<Health> Health(NpgsqlConnection connection, NpgsqlTransaction? tx, CancellationToken token)
    {
        await using var cmd = Query(connection, tx, """
            SELECT heartbeat_at,
                CASE WHEN heartbeat_at IS NULL THEN 'unseen'
                     WHEN heartbeat_at < clock_timestamp() - interval '10 seconds' THEN 'stale' ELSE 'healthy' END,
                (SELECT count(*)::integer FROM brickvault.jobs WHERE status='pending'),
                (SELECT count(*)::integer FROM brickvault.jobs WHERE status='done'),
                (SELECT count(*)::integer FROM brickvault.jobs WHERE status='blocked')
            FROM (SELECT (SELECT heartbeat_at FROM brickvault.worker_health WHERE name='reference-worker') heartbeat_at) h
            """);
        await using var r = await cmd.ExecuteReaderAsync(token);
        await r.ReadAsync(token);
        return new Health(r.GetString(1), Optional<DateTime>(r,0), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
            Migration.Current, 1);
    }

    public static async Task Run(NpgsqlDataSource db, CancellationToken token)
    {
        var lastMaintenance = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var connection = await db.OpenConnectionAsync(token);
                await using var tx = await connection.BeginTransactionAsync(token);
                // Validate the contract again on every dequeue, including after a disconnected DB returns.
                await Migration.CheckWithin(connection, tx, token);
                if (DateTimeOffset.UtcNow - lastMaintenance >= TimeSpan.FromSeconds(1))
                {
                    await using var heartbeat = Query(connection, tx, """
                        INSERT INTO brickvault.worker_health(name,heartbeat_at) VALUES ('reference-worker',clock_timestamp())
                        ON CONFLICT (name) DO UPDATE SET heartbeat_at=excluded.heartbeat_at;
                        DELETE FROM brickvault.sessions WHERE expires_at <= clock_timestamp()
                        """);
                    await heartbeat.ExecuteNonQueryAsync(token);
                    lastMaintenance = DateTimeOffset.UtcNow;
                }
                var didWork = await ProcessOne(connection, tx, token);
                await tx.CommitAsync(token);
                if (!didWork) await Task.Delay(1000, token);
            }
            catch (NpgsqlException e) when (e.IsTransient && !token.IsCancellationRequested)
            {
                Console.Error.WriteLine("foundation_worker database_unavailable");
                await Task.Delay(2000, token);
            }
        }
    }

    // Claims, the durable reference-processing receipt and acknowledgement commit together.
    // A killed worker releases the row lock and rolls back its complete attempt.
    private static async Task<bool> ProcessOne(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken token)
    {
        Guid commandId;
        bool supported;
        await using (var claim = Query(connection, tx, """
            SELECT command_id,kind,payload_version FROM brickvault.jobs WHERE status='pending'
            ORDER BY enqueued_at,command_id FOR UPDATE SKIP LOCKED LIMIT 1
            """))
        await using (var row = await claim.ExecuteReaderAsync(token))
        {
            if (!await row.ReadAsync(token)) return false;
            commandId = row.GetGuid(0);
            supported = row.GetString(1) == "record-reference" && row.GetInt32(2) == 1;
        }
        if (supported)
        {
            await using var effect = Query(connection, tx,
                "INSERT INTO brickvault.job_effects(command_id) VALUES ($1) ON CONFLICT (command_id) DO NOTHING", commandId);
            await effect.ExecuteNonQueryAsync(token);
        }
        await using var finish = Query(connection, tx,
            "UPDATE brickvault.jobs SET status=$2 WHERE command_id=$1", commandId, supported ? "done" : "blocked");
        await finish.ExecuteNonQueryAsync(token);
        return true;
    }
}
