
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record LocationLabelCommand
{
    public required Guid CommandId { get; init; }
    public required Guid LocationId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required string Label { get; init; }
}

internal sealed record LocationArchiveCommand
{
    public required Guid CommandId { get; init; }
    public required Guid LocationId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required string ConfirmedLabel { get; init; }
    public required string Reason { get; init; }
    public required long ExpectedPhysicalQuantity { get; init; }
    public required long ExpectedPositionCount { get; init; }
}

internal sealed record LocationRegistryRow(LocationRow Location, bool Archived, DateTime? ArchivedAt, Guid? ArchiveEventId,
    long PhysicalQuantity, long PositionCount);
internal sealed record LocationRegistryLabel(string Label, bool IsCanonical, int ClaimedRevision);
internal sealed record LocationRegistryEvent(Guid Id, Guid CommandId, Guid CorrelationId, int Revision,
    string Kind, string? Label, string? PreviousCode, string CanonicalCode, string Name, string? Reason,
    DateTime RecordedAt, long? PhysicalQuantity, long? PositionCount);
internal sealed record LocationRegistryPage(string Scope, DateTime AsOf, bool Complete,
    IReadOnlyList<LocationRegistryRow> Rows, Guid? NextAfter);
internal sealed record LocationLabelLookup(string Scope, DateTime AsOf, bool Complete, LocationRegistryRow Bin,
    string MatchedLabel, bool IsCanonical);
internal sealed record LocationRegistrySnapshot(string Scope, DateTime AsOf, bool Complete, LocationRegistryRow Bin,
    IReadOnlyList<LocationRegistryLabel> Labels, IReadOnlyList<LocationRegistryEvent> Events, string? NextLabelAfter, Guid? NextEventAfter);
internal sealed record LocationRegistryEvidence(string Scope, DateTime AsOf, bool Complete, Guid LocationId, LocationRegistryEvent Event);

internal sealed partial class LocationRegistry(NpgsqlDataSource db)
{
    private const long MaxExactCount = 9_007_199_254_740_991;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,47}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    private static bool IsLabel(string? value) => value is { Length: <= 48 } && LabelPattern().IsMatch(value);

    private static string LabelKey(string? value)
    {
        if (!IsLabel(value)) throw new Fault(400, "invalid_location_label", "Enter a registered label with 1 to 48 letters, digits, periods, underscores or hyphens.");
        return value!.ToUpperInvariant();
    }

    public Task<Receipt> Save(LocationLabelCommand command, Guid correlationId, CancellationToken token) =>
        Write(command.CommandId, command.LocationId, command.ExpectedRevision, command.Label, null, null, null,
            "location-label", "label-added", command, correlationId, token);

    public Task<Receipt> Archive(LocationArchiveCommand command, Guid correlationId, CancellationToken token)
    {
        if (command.Reason is not { Length: > 0 and <= 200 } || command.Reason != command.Reason.Trim() ||
            command.Reason.Any(char.IsControl) || command.ExpectedPhysicalQuantity is < 0 or > MaxExactCount ||
            command.ExpectedPositionCount is < 0 or > MaxExactCount ||
            command.ExpectedPositionCount > command.ExpectedPhysicalQuantity)
            throw new Fault(400, "invalid_location_archive", "Review the current occupancy and record why the bin is being archived.");
        return Write(command.CommandId, command.LocationId, command.ExpectedRevision, command.ConfirmedLabel, command.Reason,
            command.ExpectedPhysicalQuantity, command.ExpectedPositionCount, "location-archive", "archived", command, correlationId, token);
    }

    private async Task<Receipt> Write<T>(Guid commandId, Guid locationId, int expectedRevision, string label,
        string? reason, long? physicalQuantity, long? positionCount, string commandKind, string eventKind,
        T command, Guid correlationId, CancellationToken token)
    {
        if (commandId == Guid.Empty || locationId == Guid.Empty || expectedRevision is < 1 or int.MaxValue)
            throw new Fault(400, "invalid_location_registry_command", "Select a registered bin and its current revision.");
        _ = LabelKey(label);
        var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, Wire.Json));
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        if (await CommandJournal.Replay(connection, tx, commandId, digest, token) is { } saved) return saved;
        var receipt = new Receipt(commandId, Guid.NewGuid(), checked(expectedRevision+1), commandKind, correlationId);

        try
        {
            await using var insert = Query(connection, tx, """
                INSERT INTO brickvault.location_registry_events(
                    id,command_id,location_id,revision,kind,label,reason,physical_quantity,position_count)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
                """, receipt.EntityId, commandId, locationId, receipt.Revision, eventKind, label,
                Nullable(NpgsqlDbType.Text, reason), Nullable(NpgsqlDbType.Bigint, physicalQuantity),
                Nullable(NpgsqlDbType.Bigint, positionCount));
            await insert.ExecuteNonQueryAsync(token);
            await CommandJournal.Append(connection, tx, receipt, digest, "record-reference", token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException error) when (error.ConstraintName is "unknown_location" or "location_registry_revision" or
            "duplicate_location_label" or "location_already_archived" or "location_label_mismatch" or "location_archive_stock_changed")
        {
            throw error.ConstraintName switch
            {
                "unknown_location" => UnknownLocation(),
                "location_registry_revision" => Fault.Conflict(),
                "duplicate_location_label" => DuplicateLabel(),
                "location_already_archived" => new Fault(409, "location_already_archived", "This bin is already archived."),
                "location_archive_stock_changed" => new Fault(409, "location_archive_stock_changed",
                    "The stock in this bin changed. Reload its occupancy before confirming archival."),
                _ => new Fault(409, "location_label_mismatch", "Confirm a physical label registered to the selected bin.")
            };
        }
    }

    internal static Fault DuplicateLabel() => new(409, "duplicate_location_label", "That physical label is already registered.");
    internal static Fault ArchivedDestination() => new(409, "location_destination_archived",
        "The destination bin is archived. Select an active destination.");
    private static Fault UnknownLocation() => new(404, "unknown_location", "No bin has that identifier.");
    private static Fault InvalidCursor() => new(400, "invalid_location_registry_cursor", "Use a continuation from this bin registry read.");

    private const string BinSelect = """
        l.id,l.revision,l.code,l.name,archive.id,a.occurred_at,stock.physical_quantity,stock.position_count
        """;
    private const string BinJoins = """
        LEFT JOIN brickvault.location_registry_events archive ON archive.location_id=l.id AND archive.kind='archived'
        LEFT JOIN brickvault.audit a ON a.command_id=archive.command_id
        CROSS JOIN LATERAL brickvault.location_physical_stock(l.id) stock
        """;
    private const string EventSelect = """
        SELECT e.id,e.command_id,c.correlation_id,e.revision,e.kind,e.label,e.previous_code,e.canonical_code,e.name,
            e.reason,a.occurred_at,e.physical_quantity,e.position_count
        FROM brickvault.location_registry_events e JOIN brickvault.commands c USING(command_id)
        JOIN brickvault.audit a ON a.command_id=e.command_id
        """;

    private static LocationRegistryRow ReadBin(NpgsqlDataReader r) =>
        new(new LocationRow(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3)), !r.IsDBNull(4),
            Optional<DateTime>(r,5), Optional<Guid>(r,4), r.GetInt64(6), r.GetInt64(7));

    private static LocationRegistryEvent ReadEvent(NpgsqlDataReader r) =>
        new(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetInt32(3),r.GetString(4),
            ReadText(r,5),ReadText(r,6),r.GetString(7),r.GetString(8),ReadText(r,9),r.GetDateTime(10),
            Optional<long>(r,11),Optional<long>(r,12));

    private static string? ReadText(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static void CheckLimit(int limit)
    {
        if (limit is < 1 or > 100)
            throw new Fault(400, "invalid_location_registry_page", "Choose a page size between 1 and 100.");
    }

    private static TKey? TrimPage<T,TKey>(List<T> rows, int limit, Func<T,TKey> key) where TKey : struct
    {
        if (rows.Count <= limit) return null;
        rows.RemoveAt(limit);
        return key(rows[^1]);
    }

    private static async Task<DateTime> BeginRead(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken token)
    {
        await using (var readOnly = Query(connection, tx, "SET TRANSACTION READ ONLY"))
            await readOnly.ExecuteNonQueryAsync(token);
        await Migration.CheckWithin(connection, tx, token);
        await using var clock = Query(connection, tx, "SELECT transaction_timestamp()");
        return (DateTime)(await clock.ExecuteScalarAsync(token))!;
    }

    public async Task<LocationRegistryPage> List(Guid? after, int limit, CancellationToken token)
    {
        CheckLimit(limit);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var asOf = await BeginRead(connection, tx, token);
        if (after.HasValue)
        {
            await using var cursor = Query(connection, tx, "SELECT 1 FROM brickvault.locations WHERE id=$1", after.Value);
            if (await cursor.ExecuteScalarAsync(token) is null) throw InvalidCursor();
        }
        var rows = await Rows(connection, tx, """
            WITH bins AS MATERIALIZED (
                SELECT id,revision,code,name FROM brickvault.locations
                WHERE $1::uuid IS NULL OR id>$1 ORDER BY id LIMIT $2
            )
            SELECT
            """ + " " + BinSelect + "\nFROM bins l\n" + BinJoins + "\nORDER BY l.id",
            ReadBin, token, Nullable(NpgsqlDbType.Uuid, after), limit+1);
        var next = TrimPage(rows, limit, row => row.Location.Id);
        return new("location-registry", asOf, true, rows, next);
    }

    public async Task<LocationLabelLookup> Lookup(string label, CancellationToken token)
    {
        var key = LabelKey(label);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var asOf = await BeginRead(connection, tx, token);
        var rows = await Rows(connection, tx, "SELECT " + BinSelect + """
            ,match.label_key=l.code,CASE WHEN match.label_key=l.code THEN l.code ELSE match.label END
            FROM brickvault.location_labels match JOIN brickvault.locations l ON l.id=match.location_id
            """ + "\n" + BinJoins + "\nWHERE match.label_key=$1",
            r => new LocationLabelLookup("location-label-lookup",asOf,true,ReadBin(r),r.GetString(9),r.GetBoolean(8)), token, key);
        return rows.SingleOrDefault() ?? throw new Fault(404, "unknown_location_label", "No bin has that registered label.");
    }

    public async Task<LocationRegistrySnapshot> Detail(Guid locationId, string? labelAfter, int labelLimit,
        Guid? eventAfter, int eventLimit, CancellationToken token)
    {
        CheckLimit(labelLimit);
        CheckLimit(eventLimit);
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var asOf = await BeginRead(connection, tx, token);
        var bins = await Rows(connection, tx, "SELECT " + BinSelect + "\nFROM brickvault.locations l\n" + BinJoins
            + "\nWHERE l.id=$1", ReadBin, token, locationId);
        var bin = bins.SingleOrDefault() ?? throw UnknownLocation();
        string? key = null;
        if (labelAfter is not null)
        {
            if (!IsLabel(labelAfter)) throw InvalidCursor();
            key = LabelKey(labelAfter);
            await using var cursor = Query(connection, tx,
                "SELECT 1 FROM brickvault.location_labels WHERE location_id=$1 AND label_key=$2", locationId, key);
            if (await cursor.ExecuteScalarAsync(token) is null) throw InvalidCursor();
        }
        int? revision = null;
        if (eventAfter.HasValue)
        {
            await using var cursor = Query(connection, tx,
                "SELECT revision FROM brickvault.location_registry_events WHERE location_id=$1 AND id=$2", locationId, eventAfter.Value);
            revision = (await cursor.ExecuteScalarAsync(token)) as int? ?? throw InvalidCursor();
        }
        var labels = await Rows(connection, tx, """
            SELECT CASE WHEN label_key=$2 THEN $2 ELSE label END,label_key=$2,claimed_revision
            FROM brickvault.location_labels
            WHERE location_id=$1 AND ($3::text IS NULL OR label_key>$3) ORDER BY label_key LIMIT $4
            """, r => new LocationRegistryLabel(r.GetString(0),r.GetBoolean(1),r.GetInt32(2)), token, locationId,
            bin.Location.Code, Nullable(NpgsqlDbType.Text,key), labelLimit+1);
        string? nextLabel = null;
        if (labels.Count > labelLimit)
        {
            labels.RemoveAt(labelLimit);
            nextLabel = labels[^1].Label;
        }
        var events = await Rows(connection, tx, EventSelect + "\n" + """
            WHERE e.location_id=$1 AND ($2::integer IS NULL OR e.revision<$2) ORDER BY e.revision DESC LIMIT $3
            """, ReadEvent, token, locationId, Nullable(NpgsqlDbType.Integer,revision), eventLimit+1);
        var nextEvent = TrimPage(events, eventLimit, row => row.Id);
        return new("location-registry-detail",asOf,true,bin,labels,events,nextLabel,nextEvent);
    }

    public async Task<LocationRegistryEvidence> Evidence(Guid locationId, Guid? eventId, Guid? commandId, CancellationToken token)
    {
        if (eventId.HasValue == commandId.HasValue)
            throw new Fault(400,"invalid_location_registry_lookup","Select exactly one registry event or command.");
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var asOf = await BeginRead(connection, tx, token);
        var selector = eventId.HasValue ? "e.id" : "e.command_id";
        var events = await Rows(connection, tx, EventSelect + $"\nWHERE e.location_id=$1 AND {selector}=$2",
            ReadEvent, token, locationId, (eventId ?? commandId)!.Value);
        var matched = events.SingleOrDefault() ??
            throw new Fault(404,"unknown_location_registry_event","No matching registry event belongs to this bin.");
        return new("location-registry-receipt",asOf,true,locationId,matched);
    }
}
