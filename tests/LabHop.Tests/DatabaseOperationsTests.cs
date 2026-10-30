using System.Diagnostics;
using LabHop.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace LabHop.Tests;

/// <summary>The published application run as an operator would run it: criteria A1, D1-D3.</summary>
public sealed class DatabaseOperationsTests(PostgresServer postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record ProcessResult(int ExitCode, string Output);

    private static async Task<ProcessResult> RunAppAsync(string connectionString, string keysPath, string? standardInput, params string[] arguments)
    {
        var appPath = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(appPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(appPath);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
        start.Environment["LabHop__PublicBaseUrl"] = "https://localhost/";
        start.Environment["LabHop__KeysPath"] = keysPath;

        using var process = Process.Start(start)!;
        if (standardInput is not null)
        {
            await process.StandardInput.WriteLineAsync(standardInput);
        }

        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ProcessResult(process.ExitCode, await output + await error);
    }

    private static async Task<List<string>> PublicTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    [Fact]
    public async Task Startup_with_pending_migrations_fails_and_leaves_the_database_unchanged()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        var result = await RunAppAsync(connectionString, LabHopApp.CreateKeysPath(), standardInput: null);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pending migration", result.Output);
        Assert.Contains("migrate", result.Output);
        Assert.DoesNotContain("Unhandled exception", result.Output);
        var tables = await PublicTablesAsync(connectionString);
        Assert.DoesNotContain("Projects", tables);
        Assert.DoesNotContain("__EFMigrationsHistory", tables);
    }

    [Fact]
    public async Task Startup_with_an_unreachable_database_fails_with_a_clear_message()
    {
        const string nothingListening = "Host=127.0.0.1;Port=1;Database=labhop;Username=labhop;Password=not-a-real-secret;Timeout=2";

        var result = await RunAppAsync(nothingListening, LabHopApp.CreateKeysPath(), standardInput: null);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Cannot reach the database", result.Output);
        Assert.DoesNotContain("not-a-real-secret", result.Output);
        Assert.DoesNotContain("Unhandled exception", result.Output);
    }

    [Fact]
    public async Task Migrate_command_applies_the_schema_and_is_safe_to_repeat()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var keysPath = LabHopApp.CreateKeysPath();

        var first = await RunAppAsync(connectionString, keysPath, standardInput: null, "migrate");
        Assert.True(first.ExitCode == 0, first.Output);

        var tables = await PublicTablesAsync(connectionString);
        Assert.Contains("Projects", tables);
        Assert.Contains("ActivityEntries", tables);
        Assert.Contains("AspNetUsers", tables);
        Assert.Contains("OpenIddictTokens", tables);

        var second = await RunAppAsync(connectionString, keysPath, standardInput: null, "migrate");
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("up to date", second.Output);
    }

    [Fact]
    public async Task Cli_creates_the_account_that_can_then_sign_in()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var keysPath = LabHopApp.CreateKeysPath();
        await LabHopApp.MigrateAsync(connectionString);
        const string email = "owner@example.net";
        const string password = "A-long-enough-passw0rd";

        var created = await RunAppAsync(connectionString, keysPath, password, "create-user", "--email", email);
        Assert.True(created.ExitCode == 0, created.Output);
        Assert.DoesNotContain(password, created.Output);

        await using var app = new LabHopApp(connectionString, keysPath);
        var signIn = await new OAuthDriver(app).SubmitLoginAsync(email, password);
        Assert.Equal(System.Net.HttpStatusCode.Found, signIn.StatusCode);
    }

    [Fact]
    public async Task Cli_rejects_a_weak_password()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var keysPath = LabHopApp.CreateKeysPath();
        await LabHopApp.MigrateAsync(connectionString);

        var created = await RunAppAsync(connectionString, keysPath, "short", "create-user", "--email", "owner@example.net");

        Assert.NotEqual(0, created.ExitCode);
    }

    [Theory]
    [InlineData("http://example.net/callback")]
    [InlineData("ftp://example.net/callback")]
    [InlineData("https://example.net/callback#fragment")]
    public async Task Cli_rejects_unsafe_redirect_uris(string redirectUri)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var keysPath = LabHopApp.CreateKeysPath();
        await LabHopApp.MigrateAsync(connectionString);

        var result = await RunAppAsync(connectionString, keysPath, null, "oauth-client", "add", "--client-id", "bad", "--name", "Bad", "--redirect-uri", redirectUri);

        Assert.NotEqual(0, result.ExitCode);
        var list = await RunAppAsync(connectionString, keysPath, null, "oauth-client", "list");
        Assert.DoesNotContain("bad", list.Output);
    }

    [Fact]
    public async Task Cli_registers_lists_and_removes_a_loopback_client()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var keysPath = LabHopApp.CreateKeysPath();
        await LabHopApp.MigrateAsync(connectionString);

        var added = await RunAppAsync(connectionString, keysPath, null, "oauth-client", "add", "--client-id", "claude-code", "--name", "Claude Code", "--loopback");
        Assert.True(added.ExitCode == 0, added.Output);

        var duplicate = await RunAppAsync(connectionString, keysPath, null, "oauth-client", "add", "--client-id", "claude-code", "--name", "Other", "--loopback");
        Assert.NotEqual(0, duplicate.ExitCode);

        var list = await RunAppAsync(connectionString, keysPath, null, "oauth-client", "list");
        Assert.Contains("claude-code", list.Output);
        Assert.Contains("http://localhost/callback", list.Output);

        var removed = await RunAppAsync(connectionString, keysPath, null, "oauth-client", "remove", "--client-id", "claude-code");
        Assert.Equal(0, removed.ExitCode);
        Assert.DoesNotContain("claude-code", (await RunAppAsync(connectionString, keysPath, null, "oauth-client", "list")).Output);
    }
}
