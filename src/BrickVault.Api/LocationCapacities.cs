
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using static BrickVault.Api.Database;

namespace BrickVault.Api;

internal sealed record LocationCapacityCommand
{
    public required Guid CommandId { get; init; }
    public required Guid LocationId { get; init; }
    public required int ExpectedRevision { get; init; }
    public required long? PieceLimit { get; init; }
    public required string Reason { get; init; }
}

internal sealed record LocationCapacityState(long? PieceLimit, long? RemainingQuantity, long OverCapacityQuantity)
{
    internal static LocationCapacityState From(long? limit,long physicalQuantity) =>
        limit is long configured
            ? new(configured,Math.Max(0,configured-physicalQuantity),Math.Max(0,physicalQuantity-configured))
            : new(null,null,0);
}

internal sealed record LocationCapacityRejection(Guid LocationId,long PieceLimit,long PhysicalQuantity,
    long RemainingQuantity,long RequestedNetQuantity);

internal sealed record LocationCapacityEvent(Guid Id,Guid CommandId,Guid CorrelationId,int Revision,long? PieceLimit,
    long? PreviousPieceLimit,string Reason,long PhysicalQuantity,long PositionCount,DateTime RecordedAt);
internal sealed record LocationCapacitySnapshot(string Scope,DateTime AsOf,bool Complete,LocationRegistryRow Bin,
    IReadOnlyList<LocationCapacityEvent> Events,Guid? NextAfter);
internal sealed record LocationCapacityEvidence(string Scope,DateTime AsOf,bool Complete,Guid LocationId,LocationCapacityEvent Event);

internal sealed class LocationCapacities(NpgsqlDataSource db)
{
    private const long MaxExactCount = 9_007_199_254_740_991;
    private const string EventQuery = """
        SELECT e.id,e.command_id,c.correlation_id,e.revision,e.piece_limit,e.previous_piece_limit,e.reason,
            e.physical_quantity,e.position_count,a.occurred_at
        FROM brickvault.location_capacity_events e JOIN brickvault.commands c USING(command_id)
        JOIN brickvault.audit a ON a.command_id=e.command_id
        """;

    private static LocationCapacityEvent ReadEvent(NpgsqlDataReader row) =>
        new(row.GetGuid(0),row.GetGuid(1),row.GetGuid(2),row.GetInt32(3),Optional<long>(row,4),
            Optional<long>(row,5),row.GetString(6),row.GetInt64(7),row.GetInt64(8),row.GetDateTime(9));

    internal static Fault Rejection(PostgresException exception)
    {
        var witness = JsonSerializer.Deserialize<LocationCapacityRejection>(exception.MessageText,Wire.Json)
            ?? throw new InvalidOperationException("The capacity decision did not return its witness.");
        return new Fault(409,"location_capacity_exceeded","The selected bin does not have room for the incoming pieces.",witness);
    }

    public async Task<Receipt> Save(LocationCapacityCommand command, Guid correlationId, CancellationToken token)
    {
        if (command.CommandId==Guid.Empty || command.LocationId==Guid.Empty || command.ExpectedRevision is <1 or int.MaxValue ||
            command.PieceLimit is <1 or >MaxExactCount || command.Reason is not { Length: >0 and <=200 } ||
            command.Reason!=command.Reason.Trim() || command.Reason.Any(char.IsControl))
            throw new Fault(400,"invalid_location_capacity","Choose a positive whole-piece limit or clear it, and record the reason.");

        var payload=JsonSerializer.Serialize(command,Wire.Json);
        var digest=SHA256.HashData(Encoding.UTF8.GetBytes("location-capacity\n"+payload));
        await using var connection=await db.OpenConnectionAsync(token);
        await using var tx=await connection.BeginTransactionAsync(token);
        await Migration.CheckWithin(connection,tx,token);
        if (await CommandJournal.Replay(connection,tx,command.CommandId,digest,token) is { } replay) return replay;
        var receipt=new Receipt(command.CommandId,Guid.NewGuid(),command.ExpectedRevision+1,"location-capacity",correlationId);

        try
        {
            await using var insert=Query(connection,tx,"""
                INSERT INTO brickvault.location_capacity_events(id,command_id,location_id,revision,piece_limit,reason)
                VALUES ($1,$2,$3,$4,$5,$6)
                """,receipt.EntityId,command.CommandId,command.LocationId,receipt.Revision,
                Nullable(NpgsqlDbType.Bigint,command.PieceLimit),command.Reason);
            await insert.ExecuteNonQueryAsync(token);
            await CommandJournal.Append(connection,tx,receipt,digest,"record-reference",token);
            await tx.CommitAsync(token);
            return receipt;
        }
        catch (PostgresException error) when (error.ConstraintName is "unknown_location" or "location_registry_revision")
        {
            if (error.ConstraintName=="unknown_location") throw LocationRegistry.UnknownLocation();
            throw Fault.Conflict();
        }
    }

    public async Task<LocationCapacitySnapshot> Snapshot(Guid locationId,Guid? after,int limit,CancellationToken token)
    {
        LocationRegistry.CheckLimit(limit);
        await using var connection=await db.OpenConnectionAsync(token);
        await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        var asOf=await LocationRegistry.BeginRead(connection,tx,token);
        var bin=await LocationRegistry.ReadBin(connection,tx,locationId,true,token);
        int? revision=null;
        if (after.HasValue)
        {
            await using var anchor=Query(connection,tx,"""
                SELECT revision FROM brickvault.location_capacity_events WHERE id=$1 AND location_id=$2
                """,after.Value,locationId);
            revision=(await anchor.ExecuteScalarAsync(token)) as int? ??
                throw new Fault(400,"invalid_location_capacity_cursor","Use a continuation from this bin's capacity history.");
        }
        var events=await Rows(connection,tx,EventQuery+"\n"+"""
            WHERE e.location_id=$1 AND ($2::integer IS NULL OR e.revision<$2) ORDER BY e.revision DESC LIMIT $3
            """,ReadEvent,token,locationId,Nullable(NpgsqlDbType.Integer,revision),limit+1);
        var next=LocationRegistry.TrimPage(events,limit,row=>row.Id);
        return new("location-capacity",asOf,true,bin,events,next);
    }

    public async Task<LocationCapacityEvidence> Evidence(Guid locationId,Guid? eventId,Guid? commandId,CancellationToken token)
    {
        if (eventId.HasValue==commandId.HasValue)
            throw new Fault(400,"invalid_location_capacity_lookup","Select exactly one capacity event or command.");
        await using var connection=await db.OpenConnectionAsync(token);
        await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        var asOf=await LocationRegistry.BeginRead(connection,tx,token);
        var selector=eventId.HasValue?"e.id":"e.command_id";
        var matching=await Rows(connection,tx,EventQuery+$"\nWHERE e.location_id=$1 AND {selector}=$2",
            ReadEvent,token,locationId,(eventId??commandId)!.Value);
        var evidence=matching.SingleOrDefault() ??
            throw new Fault(404,"unknown_location_capacity_event","No matching capacity receipt belongs to this bin.");
        return new("location-capacity-receipt",asOf,true,locationId,evidence);
    }
}
