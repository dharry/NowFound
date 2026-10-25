namespace NowFound.IntegrationTests;

/// <summary>管理画面（ユーザー・グループ・フォルダ・操作ログ・システム設定）が英語で表示されること。</summary>
public sealed class AdminLocalizationTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<string> GetAsync(HttpClient client, string url, string language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptLanguage.ParseAdd(language);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Users_ListAndCreate_AreInEnglish()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();

        var list = await GetAsync(client, "/Users", "en");
        Assert.Contains("User management", list);
        Assert.Contains("Add user", list);
        Assert.Contains("Administrator", list);
        Assert.DoesNotContain("ユーザー管理", list);
        Assert.DoesNotContain("メールアドレス", list);

        var create = await GetAsync(client, "/Users/Create", "en");
        Assert.Contains("Initial password", create);
        Assert.Contains("Require a password change at first sign-in", create);
        Assert.DoesNotContain("初期パスワード", create);
    }

    [Fact]
    public async Task Groups_ShowTheDefaultGroupInEnglish_AndEditStaysReadOnly()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();

        var list = await GetAsync(client, "/Groups", "en");
        Assert.Contains("Group management", list);
        Assert.Contains("Default group", list);
        Assert.Contains("(default)", list);
        Assert.DoesNotContain("デフォルトグループ", list);
        Assert.DoesNotContain("グループ管理", list);

        var japanese = await GetAsync(client, "/Groups", "ja");
        Assert.Contains("デフォルトグループ", japanese);

        var edit = await GetAsync(client, "/Groups/Edit/1", "en");
        Assert.Contains("Edit group", edit);
        Assert.Contains("value=\"Default group\"", edit);
        Assert.Contains("readonly", edit);
        Assert.Contains("cannot be renamed or deleted", edit);
        Assert.DoesNotContain("グループ編集", edit);
    }

    [Fact]
    public async Task Folders_ListAndEdit_AreInEnglish()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();

        var list = await GetAsync(client, "/Folders", "en");
        Assert.Contains("Search folders", list);
        Assert.Contains("No search folders are registered.", list);
        Assert.DoesNotContain("検索対象フォルダ", list);

        var create = await GetAsync(client, "/Folders/Create", "en");
        Assert.Contains("Add folder", create);
        Assert.Contains("Display name", create);
        Assert.Contains("<code>/</code>", create);
        Assert.Contains("the default patterns in system settings", create);
        Assert.Contains("\\\\computer-name\\share-name", create);
        Assert.DoesNotContain("表示名", create);
        Assert.DoesNotContain("共有", create);
    }

    [Fact]
    public async Task Logs_AreInEnglish_AndAdminActionsAreTranslatedWhenViewed()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();
        await client.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "i18n@example.com"), new("Password", "Passw0rd!"), new("RequirePasswordChange", "false")]);

        var english = await GetAsync(client, "/Logs", "en");
        Assert.Contains("Operation log", english);
        Assert.Contains("Admin action", english);
        Assert.Contains("Sign-in", english);
        Assert.Contains("Added user: i18n@example.com (User)", english);
        Assert.Contains("Email address (partial match)", english);
        Assert.DoesNotContain("操作ログ", english);
        Assert.DoesNotContain("管理操作", english);

        var japanese = await GetAsync(client, "/Logs", "ja");
        Assert.Contains("ユーザー追加: i18n@example.com (ユーザー)", japanese);
        Assert.Contains("管理操作", japanese);
    }

    [Fact]
    public async Task Settings_Pages_AreInEnglish()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();

        var index = await GetAsync(client, "/Settings", "en");
        Assert.Contains("System settings", index);
        Assert.Contains("Operation log page size", index);
        Assert.DoesNotContain("システム設定", index);

        var exclude = await GetAsync(client, "/Settings/ExcludeDefaults", "en");
        Assert.Contains("Default exclude patterns", exclude);
        Assert.Contains("Restore built-in defaults", exclude);
        Assert.Contains("<code>*</code>", exclude);
        Assert.DoesNotContain("既定の除外パターン", exclude);
        Assert.DoesNotContain("組み込み", exclude);

        var kinds = await GetAsync(client, "/Settings/FileKinds", "en");
        Assert.Contains("File types and icons", kinds);
        Assert.Contains("Add type", kinds);
        Assert.Contains("Everything else", kinds);
        Assert.Contains("Remove this type", kinds);
        Assert.DoesNotContain("種類を追加", kinds);
        Assert.DoesNotContain("拡張子", kinds);
    }

    [Fact]
    public async Task Settings_SaveFlash_IsInTheCurrentLanguage_AndLogMessageStaysJapaneseInJa()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();

        // フラッシュは保存した時点の言語で作られる。英語で保存し、日本語に切り替えても操作ログは日本語で読める。
        await client.SetLanguageAsync("en");
        await client.PostFormAsync("/Settings", "/Settings", [new("PageSize", "300"), new("LogPageSize", "50")]);

        var page = await GetAsync(client, "/Settings", "en");
        Assert.Contains("Saved.", page);

        await client.SetLanguageAsync("ja");
        var logs = await GetAsync(client, "/Logs", "ja");
        Assert.Contains("ページサイズ既定値を変更: 300（操作ログ: 50）", logs);
    }
}
