using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace BrickVault.Api;

internal static class Migration
{
    private static readonly string[] Scripts = ["001_reference.sql", "002_jobs.sql", "003_receiving.sql", "004_transfers.sql", "005_stock_counts.sql", "006_build_holds.sql", "007_stock_losses.sql", "008_recounts.sql", "009_draft_backing_withdrawals.sql", "010_stock_quarantine.sql", "011_stock_shortage_closures.sql", "012_stock_loss_recovery.sql", "013_stock_part_out.sql", "014_location_registry.sql", "015_location_capacity.sql", "016_offer_currency.sql"];
    private static readonly string[] Checksums = Scripts.Select((_, index) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Script(index + 1))))).ToArray();
    public static int Current => Scripts.Length;

    // Normalize checkout line endings before fingerprinting immutable scripts.
    private static string Script(int version)
    {
        var name = "BrickVault.Api.Migrations." + Scripts[version - 1];
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Checksum(int version) => Checksums[version - 1];

    private static async Task<int> VerifiedLevel(NpgsqlConnection connection, NpgsqlTransaction? tx,
        CancellationToken token)
    {
        await using var query = new NpgsqlCommand(
            "SELECT version,checksum FROM brickvault.schema_version ORDER BY version", connection, tx);
        await using var rows = await query.ExecuteReaderAsync(token);
        var level = 0;
        while (await rows.ReadAsync(token))
        {
            level++;
            if (rows.GetInt32(0) != level || level > Current || rows.GetString(1) != Checksum(level))
                throw new StartupFault("incompatible_schema_history");
        }
        return level;
    }

    public static async Task Check(NpgsqlDataSource db, CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await CheckWithin(connection, null, token);
    }

    public static async Task CheckWithin(NpgsqlConnection connection, NpgsqlTransaction? tx, CancellationToken token)
    {
        if (tx is not null)
        {
            // A migration must wait for in-flight versioned operations. A request that follows it reads the new level.
            await using var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock_shared(471139, 1)", connection, tx);
            await guard.ExecuteNonQueryAsync(token);
        }
        if (await VerifiedLevel(connection, tx, token) != Current)
            throw new StartupFault("schema_migration_required");
    }

    public static async Task Apply(NpgsqlDataSource db, int target, CancellationToken token)
    {
        if (target < 1 || target > Current) throw new ArgumentOutOfRangeException(nameof(target));
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await using var bootstrap = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(471139, 1);
            CREATE SCHEMA IF NOT EXISTS brickvault;
            CREATE TABLE IF NOT EXISTS brickvault.schema_version (
                version integer PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            """, connection, tx);
        await bootstrap.ExecuteNonQueryAsync(token);
        var level = await VerifiedLevel(connection, tx, token);
        if (target < level) throw new StartupFault("schema_downgrade_denied");
        while (level < target)
        {
            level++;
            await using var ddl = new NpgsqlCommand(Script(level), connection, tx);
            await ddl.ExecuteNonQueryAsync(token);
            await using var record = new NpgsqlCommand(
                "INSERT INTO brickvault.schema_version(version,checksum) VALUES ($1,$2)", connection, tx);
            record.Parameters.Add(new() { Value = level });
            record.Parameters.Add(new() { Value = Checksum(level) });
            await record.ExecuteNonQueryAsync(token);
        }
        await tx.CommitAsync(token);
    }
}
