using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Logging;
using NowFound.Application.Settings;
using NowFound.Domain.Entities;

namespace NowFound.IntegrationTests;

/// <summary>インクリメンタルサーチの設定（管理者のみ）と、検索画面への反映・操作ログの扱い。</summary>
public sealed class IncrementalSearchFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        return admin;
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient admin, bool enabled, string minChars, string delayMs) =>
        admin.PostFormAsync("/Settings", "/Settings",
        [
            new("PageSize", "500"),
            new("LogPageSize", "100"),
            new("AdminListPageSize", "50"),
            new("IncrementalEnabled", enabled ? "true" : "false"),
            new("IncrementalMinChars", minChars),
            new("IncrementalDelayMs", delayMs),
        ]);

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private async Task<IncrementalSearchSettings> CurrentAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminSettingsService>()
            .GetIncrementalSearchAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Admin_SavesSettings_AndTheyAreShownOnTheSettingsPage()
    {
        var admin = await AdminAsync();

        var response = await SaveAsync(admin, enabled: false, minChars: "4", delayMs: "750");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(new IncrementalSearchSettings(false, 4, 750), await CurrentAsync());
        var page = await admin.GetStringAsync("/Settings", TestContext.Current.CancellationToken);
        Assert.Contains("value=\"4\"", page);
        Assert.Contains("value=\"750\"", page);
    }

    [Theory]
    [InlineData("0", "300")]
    [InlineData("21", "300")]
    [InlineData("2", "99")]
    [InlineData("2", "5001")]
    public async Task OutOfRangeValues_AreRejected_AndNothingChanges(string minChars, string delayMs)
    {
        var admin = await AdminAsync();

        var response = await SaveAsync(admin, enabled: false, minChars, delayMs);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IncrementalSearchSettings.Default, await CurrentAsync());
    }

    [Fact]
    public async Task NonAdmin_CannotChangeSettings()
    {
        var admin = await AdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
        [
            new("Email", "user@example.com"),
            new("Password", "Passw0rd!"),
            new("IsAdmin", "false"),
            new("RequirePasswordChange", "false"),
            new("IsEnabled", "true"),
            new("ExpiresOn", string.Empty),
        ]);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "Passw0rd!");

        // 設定画面自体が開けない（フォームもトークンも取得できない）。
        var page = await user.GetAsync("/Settings", TestContext.Current.CancellationToken);
        var post = await user.PostAsync("/Settings", new FormUrlEncodedContent(
        [
            new("PageSize", "500"), new("IncrementalEnabled", "false"), new("IncrementalMinChars", "9"), new("IncrementalDelayMs", "900"),
        ]), TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(IncrementalSearchSettings.Default, await CurrentAsync());
    }

    [Fact]
    public async Task SearchPage_ExposesTheConfiguredValuesToTheScript()
    {
        var admin = await AdminAsync();
        await SaveAsync(admin, enabled: true, minChars: "3", delayMs: "450");

        var page = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("data-incremental=\"true\"", page);
        Assert.Contains("data-incremental-min=\"3\"", page);
        Assert.Contains("data-incremental-delay=\"450\"", page);
    }

    [Fact]
    public async Task SearchPage_WhenDisabled_TellsTheScriptNotToRun()
    {
        var admin = await AdminAsync();
        await SaveAsync(admin, enabled: false, minChars: "3", delayMs: "450");

        var page = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("data-incremental=\"false\"", page);
    }

    [Fact]
    public async Task IncrementalRequests_AreNotLogged_ButNormalSearchesAre()
    {
        var admin = await AdminAsync();

        using var incremental = new HttpRequestMessage(HttpMethod.Get, "/?Conditions[0].Field=FileName&Conditions[0].Text=abc");
        incremental.Headers.Add(Web.Controllers.HomeController.IncrementalHeader, "1");
        var incrementalResponse = await admin.SendAsync(incremental, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, incrementalResponse.StatusCode);
        Assert.Equal(0, await SearchLogCountAsync());

        var normal = await admin.GetAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=abc", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
        Assert.Equal(1, await SearchLogCountAsync());
    }

    [Fact]
    public async Task IncrementalRequests_ReturnsPartialViewFragment_WithoutFullPageLayoutAndSidebar()
    {
        var admin = await AdminAsync();

        using var incremental = new HttpRequestMessage(HttpMethod.Get, "/?Conditions[0].Field=FileName&Conditions[0].Text=abc");
        incremental.Headers.Add(Web.Controllers.HomeController.IncrementalHeader, "1");
        var incrementalResponse = await admin.SendAsync(incremental, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, incrementalResponse.StatusCode);
        var partialHtml = await BodyAsync(incrementalResponse);

        // フルレイアウト（<!DOCTYPE html>, <html>, ナビバー, サイドバー, 検索ページ全体枠）が含まれていないこと
        Assert.DoesNotContain("<!DOCTYPE html>", partialHtml);
        Assert.DoesNotContain("<html", partialHtml);
        Assert.DoesNotContain("id=\"global-nav\"", partialHtml);
        Assert.DoesNotContain("id=\"tree-panel\"", partialHtml);
        Assert.DoesNotContain("id=\"folder-tree\"", partialHtml);

        // 差分置換に必要な要素（検索結果テーブル、保存クエリfields、保存ボタン状態）が含まれていること
        Assert.Contains("id=\"search-results\"", partialHtml);
        Assert.Contains("data-incremental-swap=\"query-fields\"", partialHtml);
        Assert.Contains("id=\"save-open\"", partialHtml);

        // 通常検索ではフル HTML ページが返されること
        var normal = await admin.GetAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=abc", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
        var fullHtml = await BodyAsync(normal);
        Assert.Contains("<!DOCTYPE html>", fullHtml);
        Assert.Contains("id=\"global-nav\"", fullHtml);
        Assert.Contains("id=\"tree-panel\"", fullHtml);
        Assert.Contains("id=\"search-results\"", fullHtml);
    }

    [Fact]
    public async Task IncrementalRequests_WhenValidationError_ReturnsPartialViewWithWarnings()
    {
        var admin = await AdminAsync();

        // 201文字のキーワード（上限200文字超過）
        var longText = new string('a', 201);
        using var incremental = new HttpRequestMessage(HttpMethod.Get, $"/?Conditions[0].Field=FileName&Conditions[0].Text={longText}");
        incremental.Headers.Add(Web.Controllers.HomeController.IncrementalHeader, "1");
        var incrementalResponse = await admin.SendAsync(incremental, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, incrementalResponse.StatusCode);
        var partialHtml = await BodyAsync(incrementalResponse);

        // フルレイアウトは含まれず、警告と結果テーブル領域が含まれていること
        Assert.DoesNotContain("<!DOCTYPE html>", partialHtml);
        Assert.DoesNotContain("id=\"global-nav\"", partialHtml);
        Assert.Contains("キーワードは 200 文字までです。", partialHtml);
        Assert.Contains("id=\"search-results\"", partialHtml);
    }

    [Fact]
    public async Task IncrementalRequests_WithSavedSearch_ReturnsBannerFragment()
    {
        var admin = await AdminAsync();

        // 保存済み検索を作成
        var createResponse = await admin.PostFormAsync("/SavedSearches/Create", "/",
        [
            new("Name", "テストブックマーク"),
            new("Description", "テスト用説明"),
            new("IsPublic", "false"),
            new("IsPinned", "false"),
            new("Conditions[0].Field", "FileName"),
            new("Conditions[0].Text", "test"),
            new("Operator", "And"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);

        // 最新の保存済み検索IDを取得
        var savedPage = await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken);
        var match = System.Text.RegularExpressions.Regex.Match(savedPage, @"/SavedSearches/Run/(\d+)");
        Assert.True(match.Success);
        var savedId = match.Groups[1].Value;

        // 保存済み検索IDを指定したインクリメンタルサーチ
        using var incremental = new HttpRequestMessage(HttpMethod.Get, $"/?Saved={savedId}&Conditions[0].Field=FileName&Conditions[0].Text=test2");
        incremental.Headers.Add(Web.Controllers.HomeController.IncrementalHeader, "1");
        var response = await admin.SendAsync(incremental, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var partialHtml = await BodyAsync(response);

        // バナー差分置換用の要素が含まれていること
        Assert.Contains("data-incremental-swap=\"banner\"", partialHtml);
        Assert.Contains("テストブックマーク", partialHtml);
        Assert.DoesNotContain("<!DOCTYPE html>", partialHtml);
    }

    private async Task<int> SearchLogCountAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var logs = scope.ServiceProvider.GetRequiredService<IOperationLogService>();
        var page = await logs.SearchAsync(
            new OperationLogQuery(null, null, OperationType.Search, null, 1, 100), TestContext.Current.CancellationToken);
        return page.TotalCount;
    }
}
