using System.Net;

namespace NowFound.IntegrationTests;

/// <summary>パスワード変更は「ユーザー設定」の中で行う。強制変更（初回・有効期間切れ）のときだけ専用画面を使う。</summary>
public sealed class PasswordChangeInPreferencesTests : IDisposable
{
    private const string UserEmail = "user@example.com";
    private const string UserPassword = "Passw0rd!";
    private const string NewPassword = "Passw0rd!New";

    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> UserAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
        [
            new("Email", UserEmail), new("Password", UserPassword), new("IsAdmin", "false"),
            new("RequirePasswordChange", "false"), new("IsEnabled", "true"), new("ExpiresOn", string.Empty),
        ]);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync(UserEmail, UserPassword);
        return user;
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string current, string next, string confirm) =>
        client.PostFormAsync("/Account/ChangePassword", "/Account/Preferences",
            [new("CurrentPassword", current), new("NewPassword", next), new("ConfirmPassword", confirm)]);

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static string PathOf(HttpResponseMessage response) =>
        response.Headers.Location!.IsAbsoluteUri ? response.Headers.Location.PathAndQuery : response.Headers.Location.OriginalString;

    [Fact]
    public async Task UserSettings_HasThePasswordSection_AndTheNavigationLinkIsGone()
    {
        var user = await UserAsync();

        var page = await user.GetStringAsync("/Account/Preferences", TestContext.Current.CancellationToken);

        Assert.Contains("id=\"password\"", page);
        Assert.Contains("name=\"CurrentPassword\"", page);
        Assert.Contains("name=\"NewPassword\"", page);
        Assert.Contains("name=\"ConfirmPassword\"", page);
        Assert.Contains("8 文字以上", page);
        Assert.DoesNotContain("href=\"/Account/ChangePassword\"", page);
    }

    [Fact]
    public async Task OldChangePasswordUrl_RedirectsToUserSettings_WhenNotForced()
    {
        var user = await UserAsync();

        var response = await user.GetAsync("/Account/ChangePassword", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Preferences", PathOf(response));
    }

    [Fact]
    public async Task Change_Succeeds_FromUserSettings_AndTheNewPasswordWorks()
    {
        var user = await UserAsync();

        var response = await ChangeAsync(user, UserPassword, NewPassword, NewPassword);
        var page = await user.GetStringAsync("/Account/Preferences", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Preferences#password", PathOf(response));
        Assert.Contains("パスワードを変更しました", page);
        var relogin = await _factory.CreateBrowserLikeClient().PostFormAsync("/Account/Login", "/Account/Login",
            [new("Email", UserEmail), new("Password", NewPassword)]);
        Assert.Equal(HttpStatusCode.Redirect, relogin.StatusCode);
    }

    [Fact]
    public async Task Change_Mismatch_ShowsTheErrorInThePasswordSection_OfUserSettings()
    {
        var user = await UserAsync();

        var response = await ChangeAsync(user, UserPassword, NewPassword, "Different1!");
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("一致しません", html);
        Assert.Contains("id=\"password\"", html);
        Assert.Contains("name=\"Theme\"", html); // ユーザー設定の他の項目も表示されている
    }

    [Fact]
    public async Task Change_WrongCurrentPassword_AndWeakNewPassword_AreRejected_WithMessages()
    {
        var user = await UserAsync();

        var wrongCurrent = await BodyAsync(await ChangeAsync(user, "Wr0ng-password!", NewPassword, NewPassword));
        var weak = await BodyAsync(await ChangeAsync(user, UserPassword, "short", "short"));

        Assert.Contains("正しくありません", wrongCurrent);
        Assert.Contains("8 文字以上", weak);
    }

    [Fact]
    public async Task ForcedChange_KeepsTheDedicatedPage_AndReturnsHomeAfterwards()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsync("admin@nowfound.invalid", "password123!");

        var page = await client.GetStringAsync("/Account/ChangePassword", TestContext.Current.CancellationToken);
        var settings = await client.GetAsync("/Account/Preferences", TestContext.Current.CancellationToken);
        var change = await client.PostFormAsync("/Account/ChangePassword", "/Account/ChangePassword",
            [new("CurrentPassword", "password123!"), new("NewPassword", NewPassword), new("ConfirmPassword", NewPassword)]);

        Assert.Contains("name=\"CurrentPassword\"", page);
        Assert.DoesNotContain("name=\"Theme\"", page);
        Assert.Equal("/Account/ChangePassword", PathOf(settings));
        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);
        Assert.Equal("/", PathOf(change));
    }

    [Fact]
    public async Task ForcedChange_Mismatch_StaysOnTheDedicatedPage()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsync("admin@nowfound.invalid", "password123!");

        var response = await client.PostFormAsync("/Account/ChangePassword", "/Account/ChangePassword",
            [new("CurrentPassword", "password123!"), new("NewPassword", NewPassword), new("ConfirmPassword", "Different1!")]);
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("一致しません", html);
        Assert.DoesNotContain("name=\"Theme\"", html);
    }

    [Fact]
    public async Task ForcedChangePage_IsInEnglish_WithoutJapaneseText()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsync("admin@nowfound.invalid", "password123!");
        await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Add("Accept-Language", "en");

        var page = await client.GetStringAsync("/Account/ChangePassword", TestContext.Current.CancellationToken);

        Assert.Contains("Change password", page);
        Assert.Contains("Current password", page);
        Assert.DoesNotContain("パスワード", page);
    }
}
