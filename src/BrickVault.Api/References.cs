using System.Data;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed class References(NpgsqlDataSource db)
{
    public async Task<Receipt> Save(ReferenceCommand request, Guid correlationId, CancellationToken token)
    {
        request.Validate();
        var kind = request is CatalogCommand ? "catalog" : "location";
        var digest = request.Digest(kind);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);

        // Commands with the same UUID serialize across API instances, including first submission.
        await using var guard = Query(connection, tx,
            "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 44318))", request.CommandId);
        await guard.ExecuteNonQueryAsync(token);
        await using (var find = Query(connection, tx, """
            SELECT digest,entity_id,revision,kind,correlation_id FROM brickvault.commands WHERE command_id = $1
            """, request.CommandId))
        await using (var row = await find.ExecuteReaderAsync(token))
        {
            if (await row.ReadAsync(token))
            {
                if (!CryptographicOperations.FixedTimeEquals(digest, row.GetFieldValue<byte[]>(0)))
                    throw new Fault(409, "command_mismatch", "This command ID already identifies a different request.");
                return new Receipt(request.CommandId, row.GetGuid(1), row.GetInt32(2), row.GetString(3), row.GetGuid(4));
            }
        }

        var id = request.Id ?? Guid.NewGuid();
        int revision;
        try
        {
            await using var write = request switch
            {
                CatalogCommand catalog => WriteCatalog(connection, tx, id, catalog),
                LocationCommand location => WriteLocation(connection, tx, id, location),
                _ => throw new InvalidOperationException()
            };
            revision = (await write.ExecuteScalarAsync(token)) is int version ? version : throw Fault.Conflict();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new Fault(409, "duplicate_reference", "A reference already uses that identity or location code.");
        }
        await using (var command = Query(connection, tx, """
            INSERT INTO brickvault.commands(command_id,kind,digest,entity_id,revision,correlation_id)
            VALUES ($1,$2,$3,$4,$5,$6)
            """, request.CommandId, kind, digest, id, revision, correlationId))
            await command.ExecuteNonQueryAsync(token);
        await using (var audit = Query(connection, tx,
            "INSERT INTO brickvault.audit(command_id,actor) VALUES ($1,'operator')", request.CommandId))
            await audit.ExecuteNonQueryAsync(token);
        await using (var job = Query(connection, tx, """
            INSERT INTO brickvault.jobs(command_id,kind,payload_version) VALUES ($1,'record-reference',1)
            """, request.CommandId))
            await job.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
        return new Receipt(request.CommandId, id, revision, kind, correlationId);
    }

    private static NpgsqlCommand WriteCatalog(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id,
        CatalogCommand c)
    {
        object[] values = [id, c.ItemType, c.ItemNumber, Nullable(NpgsqlDbType.Text, c.Variant), c.Name,
            Nullable(NpgsqlDbType.Integer, c.ColorId), c.ColorState, c.Condition, c.Completeness, c.Source, c.License, c.Mapping];
        if (c.ExpectedRevision == 0)
            return Query(connection, tx, """
                INSERT INTO brickvault.catalog(id,item_type,item_number,variant,name,color_id,color_state,condition,
                    completeness,source,license,mapping,revision)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,1) RETURNING revision
                """, values);
        return Query(connection, tx, """
            UPDATE brickvault.catalog SET item_type=$2,item_number=$3,variant=$4,name=$5,color_id=$6,color_state=$7,
                condition=$8,completeness=$9,source=$10,license=$11,mapping=$12,revision=revision+1
            WHERE id=$1 AND revision=$13 RETURNING revision
            """, [..values,c.ExpectedRevision]);
    }

    private static NpgsqlCommand WriteLocation(NpgsqlConnection connection, NpgsqlTransaction tx, Guid id,
        LocationCommand c) => c.ExpectedRevision == 0
        ? Query(connection, tx, """
            INSERT INTO brickvault.locations(id,code,name,revision) VALUES ($1,$2,$3,1) RETURNING revision
            """, id, c.Code.ToUpperInvariant(), c.Name)
        : Query(connection, tx, """
            UPDATE brickvault.locations SET code=$2,name=$3,revision=revision+1 WHERE id=$1 AND revision=$4 RETURNING revision
            """, id, c.Code.ToUpperInvariant(), c.Name, c.ExpectedRevision);

    private static async Task<ReferenceExport> Snapshot(NpgsqlConnection connection, NpgsqlTransaction tx,
        bool exported, CancellationToken token)
    {
        await using var timestamp = Query(connection, tx, "SELECT transaction_timestamp()");
        var asOf = (DateTime)(await timestamp.ExecuteScalarAsync(token))!;
        const string fields = """
            SELECT id,revision,item_type,item_number,variant,name,color_id,color_state,condition,completeness,source,
                license,mapping,integration_eligible FROM brickvault.catalog
            """;
        // Reference exports omit material with unresolved or restricted rights. They contain no session or audit data.
        var sql = fields + (exported ? " WHERE license IN ('operator-owned','permitted')" : "") +
            " ORDER BY item_type,item_number,id";
        var catalog = await Rows(connection, tx, sql, r => new CatalogRow(r.GetGuid(0), r.GetInt32(1),
            r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5),
            Optional<int>(r, 6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10),
            r.GetString(11), r.GetString(12), r.GetBoolean(13)), token);
        var locations = await Rows(connection, tx, "SELECT id,revision,code,name FROM brickvault.locations ORDER BY code,id",
            r => new LocationRow(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3)), token);
        return new ReferenceExport("foundation-reference", asOf, true, catalog, locations);
    }

    public async Task<ReferenceExport> Export(CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        return await Snapshot(connection, tx, true, token);
    }

    public async Task<Workspace> Workspace(CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var snapshot = await Snapshot(connection, tx, false, token);
        var audit = await Rows(connection, tx, """
            SELECT c.command_id,c.entity_id,c.revision,c.kind,c.correlation_id,a.actor,a.occurred_at
            FROM brickvault.audit a JOIN brickvault.commands c USING (command_id) ORDER BY a.occurred_at DESC,c.command_id
            """, r => new AuditRow(r.GetGuid(0),r.GetGuid(1),r.GetInt32(2),r.GetString(3),r.GetGuid(4),
                r.GetString(5),r.GetDateTime(6)), token);
        var health = await Worker.Health(connection, tx, token);
        return new Workspace(snapshot.Scope, snapshot.AsOf, snapshot.Complete, snapshot.Catalog, snapshot.Locations, audit, health);
    }

    public async Task<Diagnostic> Diagnostic(Guid correlationId, CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await using var cmd = Query(connection, null, """
            SELECT c.command_id,c.entity_id,c.revision,c.kind,c.correlation_id,j.status,j.payload_version,e.processed_at
            FROM brickvault.commands c JOIN brickvault.jobs j USING (command_id)
            LEFT JOIN brickvault.job_effects e USING (command_id) WHERE c.correlation_id=$1
            """, correlationId);
        await using var r = await cmd.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token)) throw new Fault(404, "unknown_command", "No command has that correlation ID.");
        return new Diagnostic(r.GetGuid(0),r.GetGuid(1),r.GetInt32(2),r.GetString(3),r.GetGuid(4),
            r.GetString(5),r.GetInt32(6),Optional<DateTime>(r,7));
    }
}
