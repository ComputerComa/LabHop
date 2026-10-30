using LabHop.Auth;
using LabHop.Data;
using LabHop.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LabHop.Cli;

/// <summary>Administrative commands. These need shell access on the server, which is the point.</summary>
public static class LabHopCli
{
    private const string Usage = """
        Usage:
          LabHop migrate
          LabHop create-user --email <email>          (reads the password from stdin)
          LabHop oauth-client add --client-id <id> --name <name> (--loopback | --redirect-uri <uri> ...)
          LabHop oauth-client list
          LabHop oauth-client remove --client-id <id>
        """;

    public static bool IsCommand(string[] args) => args is ["migrate", ..] or ["create-user", ..] or ["oauth-client", ..] or ["help" or "--help"];

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        try
        {
            return args switch
            {
                ["migrate"] => await MigrateAsync(scope.ServiceProvider),
                ["create-user", .. var rest] => await CreateUserAsync(rest, scope.ServiceProvider),
                ["oauth-client", "add", .. var rest] => await AddClientAsync(rest, scope.ServiceProvider),
                ["oauth-client", "list"] => await ListClientsAsync(scope.ServiceProvider),
                ["oauth-client", "remove", .. var rest] => await RemoveClientAsync(rest, scope.ServiceProvider),
                _ => ShowUsage(),
            };
        }
        catch (ServiceException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static int ShowUsage()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static async Task<int> MigrateAsync(IServiceProvider services)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            Console.WriteLine("The database is up to date.");
            return 0;
        }

        foreach (var migration in pending)
        {
            Console.WriteLine($"Applying {migration}");
        }

        await db.Database.MigrateAsync();
        Console.WriteLine("Migrations applied.");
        return 0;
    }

    private static async Task<int> CreateUserAsync(string[] args, IServiceProvider services)
    {
        var email = Option(args, "--email");
        if (email is null)
        {
            return ShowUsage();
        }

        var password = ReadPassword();
        if (string.IsNullOrEmpty(password))
        {
            await Console.Error.WriteLineAsync("A password is required on stdin.");
            return 1;
        }

        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.CreateAsync(new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true }, password);
        if (!result.Succeeded)
        {
            await Console.Error.WriteLineAsync(string.Join(Environment.NewLine, result.Errors.Select(error => error.Description)));
            return 1;
        }

        Console.WriteLine($"Created user {email}.");
        return 0;
    }

    private static async Task<int> AddClientAsync(string[] args, IServiceProvider services)
    {
        var clientId = Option(args, "--client-id");
        var name = Option(args, "--name");
        if (clientId is null || name is null)
        {
            return ShowUsage();
        }

        var redirects = args.Contains("--loopback")
            ? OAuthClientRegistry.StandardLoopbackRedirects.ToList()
            : Options(args, "--redirect-uri");

        var client = await services.GetRequiredService<OAuthClientRegistry>().AddAsync(clientId, name, redirects);
        Console.WriteLine($"Registered public client '{client.ClientId}' ({client.DisplayName}):");
        foreach (var redirect in client.RedirectUris)
        {
            Console.WriteLine($"  {redirect}");
        }

        return 0;
    }

    private static async Task<int> ListClientsAsync(IServiceProvider services)
    {
        foreach (var client in await services.GetRequiredService<OAuthClientRegistry>().ListAsync())
        {
            Console.WriteLine($"{client.ClientId}\t{client.DisplayName}\t{string.Join(' ', client.RedirectUris)}");
        }

        return 0;
    }

    private static async Task<int> RemoveClientAsync(string[] args, IServiceProvider services)
    {
        var clientId = Option(args, "--client-id");
        if (clientId is null)
        {
            return ShowUsage();
        }

        if (!await services.GetRequiredService<OAuthClientRegistry>().RemoveAsync(clientId))
        {
            await Console.Error.WriteLineAsync($"OAuth client '{clientId}' does not exist.");
            return 1;
        }

        Console.WriteLine($"Removed client '{clientId}' and revoked its access.");
        return 0;
    }

    private static string? Option(string[] args, string name) => Options(args, name).FirstOrDefault();

    private static List<string> Options(string[] args, string name) => args
        .Select((value, index) => (value, index))
        .Where(item => item.value == name && item.index + 1 < args.Length)
        .Select(item => args[item.index + 1])
        .ToList();

    private static string? ReadPassword()
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        Console.Error.Write("Password: ");
        var password = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is { Key: not ConsoleKey.Enter } key)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                password.Length = Math.Max(0, password.Length - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return password.ToString();
    }
}
