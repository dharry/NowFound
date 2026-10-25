using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NowFound.IntegrationTests;

public sealed class ThemeFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static Task<HttpResponseMessage> SavePreferencesAsync(HttpClient client, string theme, string pageSize = "") =>
        client.PostFormAsync("/Account/Preferences", "/Account/Preferences", [new("Theme", theme), new("PageSize", pageSize)]);

    [Fact]
    public async Task Anonymous_GetsDefaultTheme_AndThemeStylesheetIsServed()
    {
        var client = _factory.CreateBrowserLikeClient();

        var login = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);
        Assert.Contains("data-theme=\"light\"", login);
        Assert.Contains("css/themes.css", login);

        var css = await client.GetAsync("/css/themes.css", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        var body = await css.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("[data-theme=\"desert\"]", body);
        Assert.Contains("[data-theme=\"hybrid\"]", body);
    }

    [Fact]
    public async Task SavedTheme_IsAppliedToEveryPage_AndShownAsSelected()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        Assert.Contains("data-theme=\"light\"", await admin.GetStringAsync("/", TestContext.Current.CancellationToken));

        var saved = await SavePreferencesAsync(admin, "desert");
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        Assert.Contains("data-theme=\"desert\"", await admin.GetStringAsync("/", TestContext.Current.CancellationToken));
        Assert.Contains("data-theme=\"desert\"", await admin.GetStringAsync("/Users", TestContext.Current.CancellationToken));
        var page = await admin.GetStringAsync("/Account/Preferences", TestContext.Current.CancellationToken);
        Assert.Contains("value=\"desert\" checked", page);
        Assert.DoesNotContain("value=\"light\" checked", page);
    }

    [Fact]
    public async Task PreferencesPage_ListsAllThemes_WithPreviewCards()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var page = await admin.GetStringAsync("/Account/Preferences", TestContext.Current.CancellationToken);

        foreach (var id in new[] { "light", "dark", "blue", "solarized", "desert", "hybrid", "slack", "facebook", "teams", "discord" })
        {
            Assert.Contains($"class=\"theme-card\" data-theme=\"{id}\"", page);
        }
    }

    [Fact]
    public async Task InvalidTheme_IsRejected_AndPreviousThemeKept()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        await SavePreferencesAsync(admin, "dark");

        var bad = await SavePreferencesAsync(admin, "not-a-theme");
        var empty = await SavePreferencesAsync(admin, "");

        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("選択できないテーマ", await bad.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Contains("data-theme=\"dark\"", await admin.GetStringAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Theme_IsPerUser()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "user@example.com"), new("Password", "Passw0rd!"), new("RequirePasswordChange", "false")]);

        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "Passw0rd!");
        await SavePreferencesAsync(user, "hybrid");

        Assert.Contains("data-theme=\"hybrid\"", await user.GetStringAsync("/", TestContext.Current.CancellationToken));
        Assert.Contains("data-theme=\"light\"", await admin.GetStringAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Theme_SurvivesRelogin_AndLoginPageUsesLastTheme()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        await SavePreferencesAsync(admin, "solarized");

        await admin.PostFormAsync("/Account/Logout", "/Account/Preferences", []);
        Assert.Contains("data-theme=\"solarized\"", await admin.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken));

        await admin.LoginAsync("admin@nowfound.invalid", "NewPassw0rd!");
        Assert.Contains("data-theme=\"solarized\"", await admin.GetStringAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TamperedCookie_IsNeverEchoedIntoHtml()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
        request.Headers.Add("Cookie", "nowfound-theme=evil\"><script>alert(1)</script>");

        var html = await (await client.SendAsync(request, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("data-theme=\"light\"", html);
        Assert.DoesNotContain("alert(1)", html);
    }

    [Fact]
    public async Task Layout_HasTopAppBar_AndHighlightsCurrentDrawerItem()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var home = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);
        var users = await admin.GetStringAsync("/Users", TestContext.Current.CancellationToken);

        Assert.Contains("class=\"app-bar\"", home);
        Assert.Contains("admin@nowfound.invalid", home);
        Assert.Contains("class=\"drawer\"", home);
        Assert.Matches("drawer-item active\"\\s+href=\"/\"", home);
        Assert.Matches("drawer-item active\"\\s+href=\"/Users\"", users);
        Assert.DoesNotMatch("drawer-item active\"\\s+href=\"/\"", users);
    }
}
