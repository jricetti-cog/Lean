using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Results.Storage;

/// <summary>The results PostgreSQL database: embedded, ordered SQL migrations applied once under an advisory lock.</summary>
public sealed class ResultsDatabase(NpgsqlDataSource dataSource, ILogger<ResultsDatabase> logger)
{
    private const string MigrationPrefix = "QuantConnect.Services.Results.Migrations.";
    private const long MigrationLockId = 0x5245_5355_4C54; // "RESULT"

    public NpgsqlDataSource DataSource => dataSource;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Execute(connection, transaction, $"SELECT pg_advisory_xact_lock({MigrationLockId})", cancellationToken).ConfigureAwait(false);
        await Execute(connection, transaction,
            "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
            cancellationToken).ConfigureAwait(false);

        var applied = new HashSet<string>();
        await using (var command = new NpgsqlCommand("SELECT version FROM schema_migrations", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                applied.Add(reader.GetString(0));
            }
        }

        var assembly = typeof(ResultsDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(MigrationPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var version = Path.GetFileNameWithoutExtension(resource[MigrationPrefix.Length..]);
            if (applied.Contains(version))
            {
                continue;
            }
            await Execute(connection, transaction, ReadResource(assembly, resource), cancellationToken).ConfigureAwait(false);
            await using var record = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES (@version)", connection, transaction);
            record.Parameters.AddWithValue("version", version);
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Applied results migration {Version}", version);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retries <see cref="MigrateAsync"/> while PostgreSQL is starting.</summary>
    public async Task MigrateWithRetryAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await MigrateAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (e is NpgsqlException or System.Net.Sockets.SocketException && DateTime.UtcNow < deadline)
            {
                logger.LogWarning("Results database not reachable yet ({Error}); retrying", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Readiness: the results database answers.</summary>
public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable", e);
        }
    }
}

internal static class Sql
{
    public static NpgsqlCommand Command(this NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql) => new(sql, connection, transaction);

    public static NpgsqlCommand With(this NpgsqlCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static NpgsqlCommand WithJson(this NpgsqlCommand command, string name, object? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = ToJson(value) is { } json ? json : DBNull.Value });
        return command;
    }

    public static string? ToJson(object? value) => value switch
    {
        null => null,
        JsonNode node => node.ToJsonString(),
        string text => text,
        _ => JsonSerializer.Serialize(value, ContractJson.Options),
    };

    public static T? Json<T>(this NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? default : JsonSerializer.Deserialize<T>(reader.GetString(ordinal), ContractJson.Options);

    public static string Name<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonSerializer.Serialize(value, ContractJson.Options).Trim('"');

    public static TEnum Parse<TEnum>(string value) where TEnum : struct, Enum =>
        JsonSerializer.Deserialize<TEnum>($"\"{value}\"", ContractJson.Options);
}
