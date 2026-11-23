using Npgsql;
using NpgsqlTypes;

namespace BrickVault.Api;

internal static class Database
{
    // Nulls require an explicit PostgreSQL type even when a query context could infer one.
    internal static NpgsqlCommand Query(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql,
        params object[] args)
    {
        var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var value in args)
        {
            if (value is NpgsqlParameter parameter) command.Parameters.Add(parameter);
            else command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        return command;
    }

    internal static NpgsqlParameter Nullable(NpgsqlDbType type, object? value) =>
        new() { NpgsqlDbType = type, Value = value ?? DBNull.Value };

    internal static T? Optional<T>(NpgsqlDataReader row, int ordinal) where T : struct =>
        row.IsDBNull(ordinal) ? null : row.GetFieldValue<T>(ordinal);

    internal static async Task<List<T>> Rows<T>(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql,
        Func<NpgsqlDataReader, T> read, CancellationToken token)
    {
        await using var cmd = Query(connection, tx, sql);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        List<T> result = [];
        while (await reader.ReadAsync(token)) result.Add(read(reader));
        return result;
    }
}
