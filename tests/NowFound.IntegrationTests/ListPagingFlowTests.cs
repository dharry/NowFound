namespace NowFound.IntegrationTests;

/// <summary>ユーザー管理・グループ管理・検索対象フォルダ・ブックマークの一覧は、上と下にページ移動が付く。</summary>
public sealed class ListPagingFlowTests : IDisposable
{
    private const string PagerMarker = "aria-label=\"ページ移動\"";

    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static int Count(string html, string marker) => html.Split(marker).Length - 1;

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        return admin;
    }

    private static Task<HttpResponseMessage> SaveSettingsAsync(HttpClient client, string adminListPageSize) =>
        client.PostFormAsync("/Settings", "/Settings",
            [new("PageSize", "500"), new("LogPageSize", "100"), new("AdminListPageSize", adminListPageSize)]);

    [Fact]
    public async Task SystemSettings_SetTheAdminListPageSize_AndTheListsShowPagers()
    {
        var admin = await AdminAsync();
        var settings = await admin.GetStringAsync("/Settings", TestContext.Current.CancellationToken);
        Assert.Contains("name=\"AdminListPageSize\"", settings);
        Assert.Contains("value=\"50\"", settings);

        // ユーザー 2 名・グループ 2 件・フォルダ 2 件を作り、1 件ずつ表示する。
        foreach (var email in new[] { "a@example.com", "b@example.com" })
        {
            await admin.PostFormAsync("/Users/Create", "/Users/Create", [new("Email", email), new("Password", "Passw0rd!")]);
        }

        foreach (var name in new[] { "GA", "GB" })
        {
            await admin.PostFormAsync("/Groups/Save", "/Groups/Create", [new("Name", name)]);
        }

        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "F1"), new("Path", Path.Combine(root, "p1"))]);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "F2"), new("Path", Path.Combine(root, "p2"))]);

        Assert.DoesNotContain(PagerMarker, await admin.GetStringAsync("/Users", TestContext.Current.CancellationToken));

        await SaveSettingsAsync(admin, "1");

        foreach (var url in new[] { "/Users", "/Groups", "/Folders" })
        {
            var html = await admin.GetStringAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(2, Count(html, PagerMarker));
            Assert.Equal(2, Count(html, "次へ</a>"));
        }

        var second = await admin.GetStringAsync("/Users?page=2", TestContext.Current.CancellationToken);
        Assert.Equal(2, Count(second, "前へ</a>"));

        // 範囲外のページは最後のページに丸める。
        var beyond = await admin.GetStringAsync("/Folders?page=99", TestContext.Current.CancellationToken);
        Assert.Contains(">F2<", beyond);
        Assert.DoesNotContain(">F1<", beyond);
    }

    [Fact]
    public async Task SystemSettings_RejectsAnOutOfRangeAdminListPageSize()
    {
        var admin = await AdminAsync();

        var response = await SaveSettingsAsync(admin, "0");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var html = await admin.GetStringAsync("/Settings", TestContext.Current.CancellationToken);
        Assert.Contains("name=\"AdminListPageSize\"", html);
        Assert.Contains("value=\"50\"", html);
    }

    [Fact]
    public async Task Bookmarks_UseThePageSizeFromUserPreferences()
    {
        var admin = await AdminAsync();
        foreach (var name in new[] { "bm1", "bm2", "bm3" })
        {
            await admin.PostFormAsync("/SavedSearches/Create", "/SavedSearches",
            [
                new("Name", name), new("IsPublic", "false"), new("Operator", "And"), new("Sort", "Path"), new("Desc", "false"),
                new("Conditions[0].Field", "FileName"), new("Conditions[0].Text", "x"),
            ]);
        }

        Assert.DoesNotContain(PagerMarker, await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));

        var saved = await admin.PostFormAsync("/Account/Preferences", "/Account/Preferences",
            [new("Theme", "light"), new("PageSize", ""), new("BookmarkPageSize", "2")]);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, saved.StatusCode);
        var preferences = await admin.GetStringAsync("/Account/Preferences", TestContext.Current.CancellationToken);
        Assert.Contains("name=\"BookmarkPageSize\"", preferences);
        Assert.Contains("value=\"2\"", preferences);

        var first = await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken);
        Assert.Equal(2, Count(first, PagerMarker));
        Assert.Contains("bm1", first);
        Assert.Contains("bm2", first);
        Assert.DoesNotContain("bm3", first);

        var second = await admin.GetStringAsync("/SavedSearches?page=2", TestContext.Current.CancellationToken);
        Assert.Contains("bm3", second);
        Assert.DoesNotContain("bm1", second);
    }

    [Fact]
    public async Task OperationLog_RecordsTheAdminListPageSizeChange()
    {
        var admin = await AdminAsync();

        await SaveSettingsAsync(admin, "30");

        var logs = await admin.GetStringAsync("/Logs?Operation=AdminChange", TestContext.Current.CancellationToken);
        Assert.Contains("ページサイズ既定値を変更: 500（操作ログ: 100）（管理画面の一覧: 30）", logs);
    }
}
