using System.Net;

namespace NowFound.IntegrationTests;

public sealed class LanguageFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<string> GetAsync(HttpClient client, string url, string? acceptLanguage = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (acceptLanguage is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Anonymous_DefaultsToJapanese_AndFollowsAcceptLanguage()
    {
        var client = _factory.CreateBrowserLikeClient();

        var defaultPage = await GetAsync(client, "/Account/Login");
        var english = await GetAsync(client, "/Account/Login", "en-US,en;q=0.9");
        var unsupported = await GetAsync(client, "/Account/Login", "fr-FR");

        Assert.Contains("<html lang=\"ja\"", defaultPage);
        Assert.Contains("<html lang=\"en\"", english);
        Assert.Contains("<html lang=\"ja\"", unsupported);
    }

    [Fact]
    public async Task LanguageIsChosenInPreferences_NotInTheHeaderOrOnTheLoginPage()
    {
        var client = _factory.CreateBrowserLikeClient();
        var login = await GetAsync(client, "/Account/Login");
        Assert.DoesNotContain("name=\"language\"", login);

        await client.LoginAsAdminAsync();
        Assert.DoesNotContain("name=\"language\"", await GetAsync(client, "/"));

        var preferences = await GetAsync(client, "/Account/Preferences");
        Assert.Contains("<select id=\"Language\" name=\"Language\">", preferences);
        Assert.Contains("<option value=\"ja\" selected=\"selected\">日本語</option>", preferences);
        Assert.Contains("<option value=\"en\">English</option>", preferences);
    }

    [Fact]
    public async Task SavingPreferences_ChangesTheLanguage_ThatBeatsTheBrowserLanguage_AndIsRemembered()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();

        await client.SetLanguageAsync("en");

        var page = await GetAsync(client, "/Account/Preferences", "ja");
        Assert.Contains("<html lang=\"en\"", page);
        Assert.Contains("<option value=\"en\" selected=\"selected\">English</option>", page);
        Assert.Contains("Operation log", page);

        // 別のブラウザ（Cookie なし）でログインしても、保存済みの言語になる。
        var other = _factory.CreateBrowserLikeClient();
        await other.LoginAsync("admin@nowfound.invalid", "NewPassw0rd!");
        Assert.Contains("<html lang=\"en\"", await GetAsync(other, "/Account/Preferences", "ja"));

        // ログアウト後のログイン画面も、最後に選んだ言語（Cookie）で表示する。
        await client.PostFormAsync("/Account/Logout", "/Account/Preferences", []);
        Assert.Contains("<html lang=\"en\"", await GetAsync(client, "/Account/Login", "ja"));
    }

    [Fact]
    public async Task SavingPreferences_WithoutLanguage_OrWithAnUnknownOne_KeepsTheCurrentLanguage()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();
        await client.SetLanguageAsync("en");

        await client.PostFormAsync("/Account/Preferences", "/Account/Preferences", [new("Theme", "light"), new("PageSize", "")]);
        Assert.Contains("<html lang=\"en\"", await GetAsync(client, "/Account/Preferences"));

        var unknown = await client.PostFormAsync("/Account/Preferences", "/Account/Preferences",
            [new("Theme", "light"), new("PageSize", ""), new("Language", "xx")]);
        Assert.Equal(HttpStatusCode.Redirect, unknown.StatusCode);
        Assert.Contains("<html lang=\"en\"", await GetAsync(client, "/Account/Preferences"));
    }

    [Fact]
    public async Task Layout_EmbedsTheJavaScriptMessages()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();

        Assert.Contains("id=\"i18n-data\"", await GetAsync(client, "/"));
    }
}
