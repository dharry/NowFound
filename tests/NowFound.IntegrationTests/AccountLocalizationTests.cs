using System.Net;

namespace NowFound.IntegrationTests;

public sealed class AccountLocalizationTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient CreateEnglishClient()
    {
        var client = _factory.CreateBrowserLikeClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return client;
    }

    [Fact]
    public async Task LoginPage_IsInEnglish_AndFailuresAreEnglish()
    {
        var client = CreateEnglishClient();

        var page = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);
        Assert.Contains("NowFound sign in", page);
        Assert.Contains("Email address", page);
        Assert.Contains("Keep me signed in", page);
        Assert.DoesNotContain("ログイン", page);

        var failed = await client.PostFormAsync("/Account/Login", "/Account/Login", [new("Email", "ghost@example.com"), new("Password", "x")]);
        var html = await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The email address or password is incorrect.", html);
        Assert.DoesNotContain("正しくありません", html);
    }

    [Fact]
    public async Task PreferencesPage_ShowsEnglishThemeNamesAndLabels_AndSavedMessage()
    {
        var client = CreateEnglishClient();
        await client.LoginAsAdminAsync();

        var page = await client.GetStringAsync("/Account/Preferences", TestContext.Current.CancellationToken);
        Assert.Contains("User settings", page);
        Assert.Contains("Search results page size", page);
        Assert.Contains("Visual Studio &quot;Light&quot;", page);
        Assert.Contains("Hybrid", page);
        Assert.DoesNotContain("明るい", page);
        Assert.DoesNotContain("ユーザー設定", page);

        var saved = await client.PostFormAsync("/Account/Preferences", "/Account/Preferences", [new("Theme", "dark"), new("PageSize", "")]);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("Saved.", await client.GetStringAsync("/Account/Preferences", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangePasswordPage_AndMismatchError_AreEnglish()
    {
        var client = CreateEnglishClient();
        await client.LoginAsAdminAsync();

        var page = await client.GetStringAsync("/Account/Preferences", TestContext.Current.CancellationToken);
        Assert.Contains("Change password", page);
        Assert.Contains("Current password", page);

        var response = await client.PostFormAsync("/Account/ChangePassword", "/Account/Preferences",
            [new("CurrentPassword", "a"), new("NewPassword", "b"), new("ConfirmPassword", "c")]);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The confirmation password does not match.", html);
        Assert.DoesNotContain("一致しません", html);
    }

    [Fact]
    public async Task AccessDeniedPage_IsInEnglish()
    {
        var client = CreateEnglishClient();

        var page = await client.GetStringAsync("/Account/AccessDenied?returnUrl=%2FLogs", TestContext.Current.CancellationToken);

        Assert.Contains("You do not have permission to perform this action.", page);
        Assert.DoesNotContain("権限がありません", page);
    }

    [Fact]
    public async Task OperationLog_ShowsLoginFailureDetail_InTheViewersLanguage()
    {
        var anon = _factory.CreateBrowserLikeClient();
        await anon.PostFormAsync("/Account/Login", "/Account/Login", [new("Email", "ghost@example.com"), new("Password", "x")]);
        var admin = CreateEnglishClient();
        await admin.LoginAsAdminAsync();

        var html = await admin.GetStringAsync("/Logs", TestContext.Current.CancellationToken);

        Assert.Contains("Authentication failed", html);
        Assert.DoesNotContain("認証失敗", html);
    }
}
