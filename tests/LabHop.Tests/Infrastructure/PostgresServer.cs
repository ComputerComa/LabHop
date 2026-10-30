using System.Collections.Concurrent;
using LabHop.Tests.Infrastructure;
using Npgsql;
using Xunit;

[assembly: AssemblyFixture(typeof(PostgresServer))]

namespace LabHop.Tests.Infrastructure;

/// <summary>
/// The real PostgreSQL server named by LABHOP_TEST_POSTGRES, whose role must have CREATEDB. Tests create their own
/// labhop_test_* databases, which are dropped at the end of the run. Nothing else on the server is touched.
/// </summary>
/// <remarks>
/// The server is shared with other work, so connections are kept scarce: no pooling for administrative commands,
/// and small pools that release idle connections quickly for everything else.
/// </remarks>
public sealed class PostgresServer : IAsyncLifetime
{
    private const string VariableName = "LABHOP_TEST_POSTGRES";

    private readonly ConcurrentBag<string> createdDatabases = [];
    private string adminConnectionString = "";

    public ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{VariableName} is not set. Point it at a PostgreSQL server whose role has CREATEDB, for example: " +
                "set -a; . ./.local/postgres.env; set +a");
        }

        adminConnectionString = new NpgsqlConnectionStringBuilder(configured) { Pooling = false }.ConnectionString;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (createdDatabases.IsEmpty)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        foreach (var name in createdDatabases)
        {
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"labhop_test_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        createdDatabases.Add(name);

        return new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = name,
            Pooling = true,
            MaxPoolSize = 5,
            ConnectionIdleLifetime = 15,
            ConnectionPruningInterval = 5,
        }.ConnectionString;
    }
}
