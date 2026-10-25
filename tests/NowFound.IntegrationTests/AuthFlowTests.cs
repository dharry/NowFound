using System.Net;
using System.Text.RegularExpressions;

namespace NowFound.IntegrationTests;

public sealed partial class AuthFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private static async Task<string> GetTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        return TokenRegex().Match(html).Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, string tokenPage, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await GetTokenAsync(client, tokenPage);
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        PostFormAsync(client, "/Account/Login", "/Account/Login", new()
        {
            ["Email"] = email,
            ["Password"] = password,
        });

    [Fact]
    public async Task Anonymous_IsRedirectedToLogin()
    {
        var client = _factory.CreateBrowserLikeClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString.Split('?')[0]);
    }

    [Fact]
    public async Task Login_WrongPassword_ShowsError()
    {
        var client = _factory.CreateBrowserLikeClient();

        var response = await LoginAsync(client, "admin@nowfound.invalid", "bad-password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("正しくありません", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Login_InitialAdmin_IsForcedToChangePassword()
    {
        var client = _factory.CreateBrowserLikeClient();

        var login = await LoginAsync(client, "admin@nowfound.invalid", "password123!");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/Account/ChangePassword", login.Headers.Location!.OriginalString);

        var home = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/Account/ChangePassword", home.Headers.Location!.OriginalString);

        var change = await PostFormAsync(client, "/Account/ChangePassword", "/Account/ChangePassword", new()
        {
            ["CurrentPassword"] = "password123!",
            ["NewPassword"] = "NewPassw0rd!",
            ["ConfirmPassword"] = "NewPassw0rd!",
        });
        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);

        var homeAfter = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, homeAfter.StatusCode);

        var users = await client.GetAsync("/Users", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_ConfirmMismatch_ShowsError()
    {
        var client = _factory.CreateBrowserLikeClient();
        await LoginAsync(client, "admin@nowfound.invalid", "password123!");

        var response = await PostFormAsync(client, "/Account/ChangePassword", "/Account/ChangePassword", new()
        {
            ["CurrentPassword"] = "password123!",
            ["NewPassword"] = "NewPassw0rd!",
            ["ConfirmPassword"] = "Different1!",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("一致しません", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_WithoutAntiforgeryToken_IsRejected()
    {
        var client = _factory.CreateBrowserLikeClient();

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "admin@nowfound.invalid",
            ["Password"] = "password123!",
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
