using System.Net;
using LabHop.Tests.Infrastructure;
using Xunit;

namespace LabHop.Tests;

/// <summary>Browser authentication: criteria A1-A4.</summary>
public sealed class AccountTests(LabHopAppFixture fixture) : IClassFixture<LabHopAppFixture>
{
    private readonly LabHopApp app = fixture.App;
    private readonly OAuthDriver driver = new(fixture.App);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/projects")]
    [InlineData("/projects/new")]
    [InlineData("/projects/0198f000-0000-7000-8000-000000000000")]
    [InlineData("/agents")]
    [InlineData("/Account/Manage/ChangePassword")]
    public async Task Anonymous_browser_is_sent_to_the_login_page(string path)
    {
        var response = await app.CreateBrowser().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/Account/Register")]
    [InlineData("/register")]
    [InlineData("/connect/register")]
    public async Task No_endpoint_registers_users(string path)
    {
        var email = $"walk-in-{Guid.NewGuid():N}@example.net";
        const string password = "correct-horse-battery-staple";
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Password"] = password, ["email"] = email, ["password"] = password });

        var response = await app.CreateBrowser().PostAsync(path, form, Ct);
        Assert.False(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}");

        var signIn = await driver.SubmitLoginAsync(email, password);
        Assert.Contains("Invalid login attempt", await signIn.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Correct_password_signs_in()
    {
        var user = await app.CreateUserAsync();

        var login = await driver.SubmitLoginAsync(user.Email, user.Password);
        Assert.Equal(HttpStatusCode.Found, login.StatusCode);

        var projects = await driver.Browser.GetAsync("/projects", Ct);
        Assert.Equal(HttpStatusCode.OK, projects.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_repeated_failures_lock_the_account()
    {
        var user = await app.CreateUserAsync();

        var first = await driver.SubmitLoginAsync(user.Email, "not-the-password");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("Invalid login attempt", await first.Content.ReadAsStringAsync(Ct));

        var last = first;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            last = await driver.SubmitLoginAsync(user.Email, "not-the-password");
        }

        Assert.Equal(HttpStatusCode.Found, last.StatusCode);
        Assert.Contains("Account/Lockout", last.Headers.Location!.ToString());

        // Even the right password is refused while locked.
        var correct = await driver.SubmitLoginAsync(user.Email, user.Password);
        Assert.Contains("Account/Lockout", correct.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("https://evil.example.net/")]
    [InlineData("//evil.example.net/")]
    [InlineData("/\\evil.example.net/")]
    [InlineData("https:evil.example.net")]
    public async Task Return_url_cannot_redirect_off_site(string returnUrl)
    {
        var user = await app.CreateUserAsync();

        var response = await driver.SubmitLoginAsync(user.Email, user.Password, returnUrl);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var destination = new Uri(app.BaseAddress, response.Headers.Location!);
        Assert.Equal(app.BaseAddress.Host, destination.Host);
    }

    [Fact]
    public async Task Logout_requires_an_antiforgery_token()
    {
        var user = await app.CreateUserAsync();
        await driver.SignInAsync(user);

        var forged = await driver.Browser.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        var stillSignedIn = await driver.Browser.GetAsync("/projects", Ct);
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
    }
}
