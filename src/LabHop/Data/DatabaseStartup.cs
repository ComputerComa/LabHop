using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LabHop.Data;

/// <summary>The database cannot serve this version of the application. The message says what to do.</summary>
public sealed class DatabaseNotReadyException(string message) : Exception(message);

public static class DatabaseStartup
{
    /// <summary>
    /// Refuses to serve against a schema the code does not expect. Migrations run only through the
    /// explicit <c>migrate</c> command, so an upgrade can never change production data by restarting.
    /// </summary>
    public static async Task EnsureNoPendingMigrationsAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        List<string> pending;
        try
        {
            pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException { InnerException: NpgsqlException })
        {
            throw new DatabaseNotReadyException($"Cannot reach the database: {exception.GetBaseException().Message}");
        }

        if (pending.Count > 0)
        {
            throw new DatabaseNotReadyException(
                $"The database has {pending.Count} pending migration(s): {string.Join(", ", pending)}. " +
                "Back up the database, then run the 'migrate' command before starting the service.");
        }
    }
}
