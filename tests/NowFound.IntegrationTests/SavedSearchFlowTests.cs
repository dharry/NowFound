using System.Net;
using System.Text.RegularExpressions;

namespace NowFound.IntegrationTests;

public sealed partial class SavedSearchFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [GeneratedRegex(@"Saved=(\d+)")]
    private static partial Regex SavedIdRegex();

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        return admin;
    }

    private async Task<HttpClient> UserAsync(HttpClient admin, string email)
    {
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", email), new("Password", "Passw0rd!"), new("RequirePasswordChange", "false")]);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync(email, "Passw0rd!");
        return user;
    }

    /// <summary>検索画面の「保存」ダイアログと同じ内容を送る。保存後の遷移先 URL と、採番された ID を返す。</summary>
    private static async Task<(HttpResponseMessage Response, int? Id)> SaveAsync(
        HttpClient client, string name, bool isPublic = false, string keyword = "report", bool pinned = false)
    {
        List<KeyValuePair<string, string>> fields =
        [
            new("Name", name),
            new("IsPublic", isPublic ? "true" : "false"),
            new("Operator", "And"),
            new("Sort", "Path"),
            new("Desc", "false"),
        ];
        if (pinned)
        {
            fields.Add(new("IsPinned", "true"));
        }

        if (keyword.Length > 0)
        {
            fields.Add(new("Conditions[0].Field", "FileName"));
            fields.Add(new("Conditions[0].Text", keyword));
        }

        // トークンは検索画面（保存ダイアログのフォーム）から取る。
        var response = await client.PostFormAsync("/SavedSearches/Create", "/", fields);
        var match = SavedIdRegex().Match(response.Headers.Location?.ToString() ?? string.Empty);
        return (response, match.Success ? int.Parse(match.Groups[1].Value) : null);
    }

    [Fact]
    public async Task Save_Then_BannerAndListsShowIt()
    {
        var admin = await AdminAsync();

        var (response, id) = await SaveAsync(admin, "月次レポート");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(id);

        var page = await admin.GetStringAsync(response.Headers.Location!.ToString(), TestContext.Current.CancellationToken);
        Assert.Contains("「月次レポート」を保存しました。", page);
        Assert.Contains("保存した検索:", page);
        Assert.Contains("上書き保存", page);
        Assert.Contains("value=\"report\"", page);   // 条件が検索バーに復元される
        Assert.Contains("月次レポート", await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Run_RedirectsToSearchWithSavedConditions()
    {
        var admin = await AdminAsync();
        var (_, id) = await SaveAsync(admin, "r", keyword: "needle");

        var run = await admin.GetAsync($"/SavedSearches/Run?id={id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, run.StatusCode);
        var location = Uri.UnescapeDataString(run.Headers.Location!.ToString());
        Assert.Contains("Conditions[0].Text=needle", location);
        Assert.Contains($"Saved={id}", location);
        var page = await admin.GetStringAsync(run.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Contains("value=\"needle\"", page);
    }

    [Fact]
    public async Task SearchPage_ListsSavedSearchesInSidePanel_AndSaveButtonNeedsAQuery()
    {
        var admin = await AdminAsync();
        await SaveAsync(admin, "サイドに出る検索");

        var empty = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);
        var withQuery = await admin.GetStringAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=abc", TestContext.Current.CancellationToken);

        Assert.Contains("サイドに出る検索", empty);
        Assert.Matches("id=\"save-open\"[^>]*disabled", empty);
        Assert.DoesNotMatch("id=\"save-open\"[^>]*disabled", withQuery);
        Assert.Contains("id=\"save-dialog\"", withQuery);
    }

    [Fact]
    public async Task Save_WithoutAnyCondition_IsRejectedWithMessage()
    {
        var admin = await AdminAsync();

        var (response, id) = await SaveAsync(admin, "empty", keyword: "");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(id);
        var page = await admin.GetStringAsync(response.Headers.Location!.ToString(), TestContext.Current.CancellationToken);
        Assert.Contains("保存できる検索条件がありません", page);
        Assert.DoesNotContain("empty", await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrivateBookmarks_AreInvisibleToOthers_EvenAdmins()
    {
        var admin = await AdminAsync();
        var user = await UserAsync(admin, "user@example.com");
        var (_, id) = await SaveAsync(user, "ユーザーの秘密");

        Assert.Contains("ユーザーの秘密", await user.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));
        Assert.DoesNotContain("ユーザーの秘密", await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));
        Assert.DoesNotContain("ユーザーの秘密", await admin.GetStringAsync("/", TestContext.Current.CancellationToken));

        var run = await admin.GetAsync($"/SavedSearches/Run?id={id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, run.StatusCode);
        Assert.EndsWith("/SavedSearches", run.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/SavedSearches/Edit/{id}", TestContext.Current.CancellationToken)).StatusCode);

        await admin.PostFormAsync($"/SavedSearches/Delete?id={id}", "/SavedSearches", []);
        Assert.Contains("ユーザーの秘密", await user.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublicBookmarks_OnlyAdminCanCreate_EveryoneCanRun_OnlyAdminCanChange()
    {
        var admin = await AdminAsync();
        var user = await UserAsync(admin, "user@example.com");

        // 一般ユーザーが公開で保存しようとしても作成されない。
        var (denied, deniedId) = await SaveAsync(user, "勝手に公開", isPublic: true);
        Assert.Null(deniedId);
        Assert.Contains("管理者のみ", await user.GetStringAsync(denied.Headers.Location!.ToString(), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("勝手に公開", await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));

        // 管理者は公開で保存でき、一般ユーザーにも見えて実行できる。
        var (_, publicId) = await SaveAsync(admin, "全員の検索", isPublic: true, keyword: "shared");
        var userList = await user.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken);
        Assert.Contains("全員の検索", userList);
        Assert.Contains("管理者のみ可能です", userList);
        Assert.DoesNotContain($"/SavedSearches/Delete?id={publicId}", userList);
        Assert.DoesNotContain($"/SavedSearches/Edit/{publicId}", userList);
        Assert.Contains("全員の検索", await user.GetStringAsync("/", TestContext.Current.CancellationToken));
        var run = await user.GetAsync($"/SavedSearches/Run?id={publicId}", TestContext.Current.CancellationToken);
        Assert.Contains("Saved=", run.Headers.Location!.ToString());

        // 一般ユーザーからは変更・削除できない（バナーにも上書きボタンが出ない）。
        var banner = await user.GetStringAsync(run.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Contains("保存した検索:", banner);
        Assert.DoesNotContain("上書き保存", banner);
        await user.PostFormAsync($"/SavedSearches/Delete?id={publicId}", "/SavedSearches", []);
        Assert.Contains("全員の検索", await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));

        var userEdit = await user.GetAsync($"/SavedSearches/Edit/{publicId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, userEdit.StatusCode);

        // 管理者は削除できる。
        await admin.PostFormAsync($"/SavedSearches/Delete?id={publicId}", "/SavedSearches", []);
        Assert.DoesNotContain("全員の検索", await user.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Overwrite_ReplacesConditions_AndKeepsTheName()
    {
        var admin = await AdminAsync();
        var (_, id) = await SaveAsync(admin, "更新対象", keyword: "before");

        var overwrite = await admin.PostFormAsync($"/SavedSearches/Overwrite?id={id}", $"/?Saved={id}&Conditions[0].Field=FileName&Conditions[0].Text=before",
        [
            new("Operator", "Or"),
            new("Sort", "Name"),
            new("Desc", "true"),
            new("Conditions[0].Field", "Extension"),
            new("Conditions[0].Text", "pdf"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, overwrite.StatusCode);
        Assert.Contains("上書き保存しました", await admin.GetStringAsync(overwrite.Headers.Location!.ToString(), TestContext.Current.CancellationToken));

        var run = await admin.GetAsync($"/SavedSearches/Run?id={id}", TestContext.Current.CancellationToken);
        var location = Uri.UnescapeDataString(run.Headers.Location!.ToString());
        Assert.Contains("Conditions[0].Text=pdf", location);
        Assert.Contains("Operator=Or", location);
        Assert.Contains("Sort=Name", location);
        Assert.Contains("更新対象", await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Edit_RenamesAndPins_AndEnforcesVisibilityRules()
    {
        var admin = await AdminAsync();
        var user = await UserAsync(admin, "user@example.com");
        var (_, userId) = await SaveAsync(user, "元の名前");

        var editPage = await user.GetStringAsync($"/SavedSearches/Edit/{userId}", TestContext.Current.CancellationToken);
        Assert.Contains("公開にできるのは管理者のみです", editPage);

        var saved = await user.PostFormAsync("/SavedSearches/Save", $"/SavedSearches/Edit/{userId}",
            [new("Id", userId.ToString()!), new("Name", "新しい名前"), new("IsPublic", "false"), new("IsPinned", "true"), .. KeywordFields("report")]);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var list = await user.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken);
        Assert.Contains("新しい名前", list);
        // 絵文字は HTML エンコーダーにより数値参照（&#x1F4CC;）で出力されることがある（表示は同じ）。
        Assert.True(list.Contains("📌") || list.Contains("&#x1F4CC;"), "固定のマークが表示されていない");

        // 一般ユーザーが公開にしようとしても失敗する（画面を迂回して直接送った場合）。
        var hack = await user.PostFormAsync("/SavedSearches/Save", $"/SavedSearches/Edit/{userId}",
            [new("Id", userId.ToString()!), new("Name", "新しい名前"), new("IsPublic", "true"), .. KeywordFields("report")]);
        Assert.Equal(HttpStatusCode.OK, hack.StatusCode);
        Assert.Contains("管理者のみ", await hack.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("新しい名前", await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken));
    }

    private static KeyValuePair<string, string>[] KeywordFields(string keyword) =>
    [
        new("Operator", "And"),
        new("Sort", "Path"),
        new("Desc", "false"),
        new("Conditions[0].Field", "FileName"),
        new("Conditions[0].Text", keyword),
    ];

    [Fact]
    public async Task Edit_ChangesFolderAndConditions_AndRejectsEmptyQuery()
    {
        var admin = await AdminAsync();
        var (_, id) = await SaveAsync(admin, "条件編集", keyword: "before");

        var editPage = await admin.GetStringAsync($"/SavedSearches/Edit/{id}", TestContext.Current.CancellationToken);
        Assert.Contains("value=\"before\"", editPage);
        Assert.Contains("対象フォルダ", editPage);
        Assert.DoesNotContain("SavedSearches.Edit.", editPage);

        var saved = await admin.PostFormAsync("/SavedSearches/Save", $"/SavedSearches/Edit/{id}",
        [
            new("Id", id.ToString()!), new("Name", "条件編集"), new("IsPublic", "false"),
            new("Operator", "Or"), new("Sort", "Name"), new("Desc", "true"),
            new("Conditions[0].Field", "Extension"), new("Conditions[0].Text", "pdf"),
            new("Conditions[1].Field", "FileName"), new("Conditions[1].Text", "memo"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        var run = await admin.GetAsync($"/SavedSearches/Run?id={id}", TestContext.Current.CancellationToken);
        var location = Uri.UnescapeDataString(run.Headers.Location!.ToString());
        Assert.Contains("Conditions[0].Text=pdf", location);
        Assert.Contains("Conditions[1].Text=memo", location);
        Assert.Contains("Operator=Or", location);

        var empty = await admin.PostFormAsync("/SavedSearches/Save", $"/SavedSearches/Edit/{id}",
            [new("Id", id.ToString()!), new("Name", "条件編集"), new("IsPublic", "false")]);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
    }

    [Fact]
    public async Task DuplicateName_ShowsErrorOnEdit()
    {
        var admin = await AdminAsync();
        await SaveAsync(admin, "A");
        var (_, secondId) = await SaveAsync(admin, "B");

        var response = await admin.PostFormAsync("/SavedSearches/Save", $"/SavedSearches/Edit/{secondId}",
            [new("Id", secondId.ToString()!), new("Name", "A"), new("IsPublic", "false"), .. KeywordFields("report")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("同じ名前", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublicChanges_AreWrittenToOperationLog_PrivateOnesAreNot()
    {
        var admin = await AdminAsync();
        await SaveAsync(admin, "ひみつ", isPublic: false);
        var (_, publicId) = await SaveAsync(admin, "みんなの", isPublic: true);
        await admin.PostFormAsync($"/SavedSearches/Delete?id={publicId}", "/SavedSearches", []);

        var logs = await admin.GetStringAsync("/Logs?Operation=AdminChange", TestContext.Current.CancellationToken);

        Assert.Contains("パブリックブックマーク追加: みんなの", logs);
        Assert.Contains("パブリックブックマーク削除: みんなの", logs);
        Assert.DoesNotContain("ひみつ", logs);
    }

    [Fact]
    public async Task RunningASavedSearch_IsLoggedWithItsName()
    {
        var admin = await AdminAsync();
        var (_, id) = await SaveAsync(admin, "ログ確認", keyword: "logged-word");
        var run = await admin.GetAsync($"/SavedSearches/Run?id={id}", TestContext.Current.CancellationToken);
        await admin.GetAsync(run.Headers.Location, TestContext.Current.CancellationToken);

        var logs = await admin.GetStringAsync("/Logs?Operation=Search", TestContext.Current.CancellationToken);

        Assert.Contains("logged-word", logs);
        Assert.Contains("保存検索", logs);
        Assert.Contains("ログ確認", logs);
    }

    [Fact]
    public async Task UnknownFolder_ShowsNoticeInsteadOfSilentlyEmpty()
    {
        var admin = await AdminAsync();

        var page = await admin.GetStringAsync("/?FolderId=999", TestContext.Current.CancellationToken);

        Assert.Contains("指定されたフォルダは存在しないか、アクセス権がありません", page);
    }

    [Fact]
    public async Task Navigation_HasSavedSearchesItem()
    {
        var admin = await AdminAsync();

        var page = await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/SavedSearches\"", page);
        Assert.Matches("drawer-item active\"\\s+href=\"/SavedSearches\"", page);
    }
}
