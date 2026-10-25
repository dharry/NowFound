using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.IntegrationTests;

public sealed partial class FileKindSettingsFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private const string SettingsPage = "/Settings/FileKinds";

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        return admin;
    }

    /// <summary>画面のフォームと同じ形（Rows[i].Key / Icon / Label / Extensions）で送る。</summary>
    private static Task<HttpResponseMessage> PostKindsAsync(HttpClient client, params (string? Key, string Icon, string Label, string Extensions)[] rows)
    {
        List<KeyValuePair<string, string>> fields = [];
        for (var i = 0; i < rows.Length; i++)
        {
            fields.Add(new($"Rows[{i}].Key", rows[i].Key ?? string.Empty));
            fields.Add(new($"Rows[{i}].Icon", rows[i].Icon));
            fields.Add(new($"Rows[{i}].Label", rows[i].Label));
            fields.Add(new($"Rows[{i}].Extensions", rows[i].Extensions));
        }

        return client.PostFormAsync(SettingsPage, SettingsPage, fields);
    }

    private static (string? Key, string Icon, string Label, string Extensions) Folder(string icon = "📁", string label = "フォルダ") => ("folder", icon, label, "");

    private static (string? Key, string Icon, string Label, string Extensions) Generic(string icon = "📃", string label = "ファイル") => ("file", icon, label, "");

    /// <summary>検索対象フォルダを登録し、指定の名前のファイルを files.db に投入する。</summary>
    private async Task SeedFilesAsync(HttpClient admin, params string[] names)
    {
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "Temp"), new("Path", root)]);

        await using var scope = _factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var directory = new DirectoryEntry { SearchFolderId = 1, Path = root };
        files.Directories.Add(directory);
        await files.SaveChangesAsync();
        var now = DateTime.UtcNow;
        files.Files.AddRange(names.Select(name => new FileEntry
        {
            SearchFolderId = 1,
            DirectoryId = directory.Id,
            Name = name,
            Extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant(),
            Size = 1,
            ModifiedAt = now,
            CreatedAt = now,
        }));
        await files.SaveChangesAsync();
    }

    [GeneratedRegex("<span class=\"file-icon\" data-kind=\"(?<kind>[^\"]*)\" title=\"(?<label>[^\"]*)\" aria-hidden=\"true\">(?<icon>[^<]*)</span>")]
    private static partial Regex IconRegex();

    private static string IconOf(string html, string fileName)
    {
        // 名前欄のセルは「アイコン + 見えないラベル + ファイル名」の順。ファイル名の直前にあるアイコンを取る。
        var index = html.IndexOf(fileName, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{fileName} が結果にない");
        var match = IconRegex().Matches(html[..index]).Last();
        return $"{match.Groups["kind"].Value}|{match.Groups["label"].Value}|{match.Groups["icon"].Value}";
    }

    // ---- 権限 ----

    [Fact]
    public async Task Page_RequiresAnAdministrator()
    {
        var anonymous = _factory.CreateBrowserLikeClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(SettingsPage, TestContext.Current.CancellationToken)).StatusCode);

        var admin = await AdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "user@example.com"), new("Password", "Passw0rd!"), new("RequirePasswordChange", "false")]);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "Passw0rd!");

        var denied = await user.GetAsync(SettingsPage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("AccessDenied", denied.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(SettingsPage, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task NonAdmin_CannotSaveOrReset()
    {
        var admin = await AdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "user@example.com"), new("Password", "Passw0rd!"), new("RequirePasswordChange", "false")]);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "Passw0rd!");

        // 画面からは送れないため、トークンは管理者権限のない別ページから取る。
        var save = await user.PostFormAsync(SettingsPage, "/Account/Preferences",
            [new("Rows[0].Key", "folder"), new("Rows[0].Icon", "X"), new("Rows[0].Label", "改ざん")]);
        var reset = await user.PostFormAsync("/Settings/ResetFileKinds", "/Account/Preferences", []);

        Assert.Contains("AccessDenied", save.Headers.Location!.OriginalString);
        Assert.Contains("AccessDenied", reset.Headers.Location!.OriginalString);
        Assert.DoesNotContain("改ざん", await admin.GetStringAsync(SettingsPage, TestContext.Current.CancellationToken));
    }

    // ---- 表示 ----

    [Fact]
    public async Task SettingsIndex_LinksToTheFileKindPage()
    {
        var admin = await AdminAsync();

        var html = await admin.GetStringAsync("/Settings", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/Settings/FileKinds\"", html);
        Assert.Contains("ファイルの種類とアイコン", html);
    }

    [Fact]
    public async Task Page_ShowsTheBuiltInDefaults_WhenNothingWasChanged()
    {
        var admin = await AdminAsync();

        var html = await admin.GetStringAsync(SettingsPage, TestContext.Current.CancellationToken);

        Assert.Contains("現在は標準の設定を使っています", html);
        Assert.Contains("name=\"Rows[0].Key\" value=\"folder\"", html);
        Assert.Contains("name=\"Rows[1].Key\" value=\"file\"", html);
        Assert.Contains("テキストファイル", html);
        Assert.Contains("txt, log, conf, config", html);
        Assert.Contains("オフィスファイル", html);
        Assert.Matches("<button type=\"submit\" class=\"secondary\" disabled=\"disabled\">標準の設定に戻す</button>", html);
        Assert.Contains("id=\"icon-suggestions\"", html);
    }

    // ---- 保存と反映 ----

    [Fact]
    public async Task SavedKinds_AreUsedByTheSearchResults_ForEveryUser()
    {
        var admin = await AdminAsync();
        await SeedFilesAsync(admin, "app.trace", "notes.txt", "pic.png");

        var saved = await PostKindsAsync(admin,
            Folder(),
            Generic("❔", "その他"),
            (null, "📜", "ログ", "log, TRACE"),
            ("text", "TXT", "テキスト", "txt"));

        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var results = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal("text|テキスト|TXT", IconOf(results, "notes.txt"));
        Assert.StartsWith("custom-", IconOf(results, "app.trace"));
        Assert.EndsWith("|ログ|📜".Replace("📜", "&#x1F4DC;"), IconOf(results, "app.trace"));
        // png は定義から外したため、「その他」の汎用アイコン（❔ は BMP 外ではないのでそのまま出る）。
        Assert.Equal("file|その他|❔", IconOf(results, "pic.png"));

        // 一般ユーザー（グループ経由で検索対象フォルダを閲覧できる）にも、同じ設定が使われる。
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "user@example.com"), new("Password", "Passw0rd!"), new("RequirePasswordChange", "false")]);
        string userId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            userId = (await scope.ServiceProvider.GetRequiredService<NowFound.Application.Users.IUserAdminService>().ListAsync(TestContext.Current.CancellationToken))
                .Single(u => u.Email == "user@example.com").Id;
        }

        await admin.PostFormAsync("/Groups/Save", "/Groups/Create", [new("Name", "閲覧者"), new("UserIds", userId), new("FolderIds", "1")]);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "Passw0rd!");

        var userResults = await user.GetStringAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal("text|テキスト|TXT", IconOf(userResults, "notes.txt"));
        Assert.StartsWith("custom-", IconOf(userResults, "app.trace"));
        Assert.Equal("file|その他|❔", IconOf(userResults, "pic.png"));
    }

    [Fact]
    public async Task Page_AfterSaving_ShowsTheEditedValues_AndEnablesReset()
    {
        var admin = await AdminAsync();

        var saved = await PostKindsAsync(admin, Folder("🗂️", "ディレクトリ"), Generic(), (null, "📜", "ログ", "log, trace"));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var html = await admin.GetStringAsync(SettingsPage, TestContext.Current.CancellationToken);

        Assert.Contains("現在は設定を変更済みです", html);
        Assert.Contains("value=\"ディレクトリ\"", html);
        Assert.Contains("log, trace", html);
        Assert.DoesNotContain("テキストファイル", html);
        Assert.DoesNotMatch("class=\"secondary\" disabled=\"disabled\">標準の設定に戻す", html);
    }

    [Fact]
    public async Task Reset_RestoresTheDefaults_InTheEditorAndTheResults()
    {
        var admin = await AdminAsync();
        await SeedFilesAsync(admin, "notes.txt");
        await PostKindsAsync(admin, Folder(), Generic(), ("text", "TXT", "テキスト", "txt"));
        Assert.Contains("TXT", IconOf(await admin.GetStringAsync("/", TestContext.Current.CancellationToken), "notes.txt"));

        var reset = await admin.PostFormAsync("/Settings/ResetFileKinds", SettingsPage, []);

        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        var page = await admin.GetStringAsync(SettingsPage, TestContext.Current.CancellationToken);
        Assert.Contains("現在は標準の設定を使っています", page);
        Assert.Contains("既定の設定に戻しました", page);
        Assert.Equal("text|テキストファイル|&#x1F4DD;", IconOf(await admin.GetStringAsync("/", TestContext.Current.CancellationToken), "notes.txt"));
    }

    [Fact]
    public async Task EveryExtensionSeparatorStyleIsAccepted()
    {
        var admin = await AdminAsync();
        await SeedFilesAsync(admin, "a.alpha", "b.beta", "c.gamma", "d.delta", "e.eps");

        await PostKindsAsync(admin, Folder(), Generic(), (null, "★", "区切りいろいろ", "*.alpha, .BETA;gamma、delta\r\neps"));

        var results = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);
        foreach (var name in new[] { "a.alpha", "b.beta", "c.gamma", "d.delta", "e.eps" })
        {
            Assert.EndsWith("|区切りいろいろ|★", IconOf(results, name));
        }
    }

    // ---- 入力エラー ----

    [Fact]
    public async Task DuplicateExtension_IsRejected_WithAMessage_AndKeepsTheUsersInput()
    {
        var admin = await AdminAsync();

        var response = await PostKindsAsync(admin, Folder(), Generic(),
            (null, "A", "種類A", "dup, only-a"), (null, "B", "種類B", "DUP"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("拡張子「dup」が「種類A」と「種類B」の両方に指定されています", html);
        // 入力した内容はそのまま画面に残り、続けて直せる。
        Assert.Contains("value=\"種類A\"", html);
        Assert.Contains("value=\"種類B\"", html);
        Assert.Contains("dup, only-a", html);
        // 何も保存されていない。
        Assert.Contains("現在は標準の設定を使っています", await admin.GetStringAsync(SettingsPage, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("tar.gz", "ドットを含められません")]
    [InlineData("a/b", "使えない文字")]
    [InlineData("a?b", "使えない文字")]
    public async Task UnusableExtensions_AreRejected(string extensions, string expectedMessage)
    {
        var admin = await AdminAsync();

        var response = await PostKindsAsync(admin, Folder(), Generic(), (null, "A", "種類A", extensions));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedMessage, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BlankNameOrIcon_AndTooLongIcon_AreRejected()
    {
        var admin = await AdminAsync();

        var blank = await PostKindsAsync(admin, Folder(), Generic(), (null, "", "", "a"));
        var tooLong = await PostKindsAsync(admin, Folder(), Generic(), (null, "123456789", "長い", "a"));

        var blankHtml = await blank.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("名前は必須です", blankHtml);
        Assert.Contains("アイコンは必須です", blankHtml);
        Assert.Contains("8 文字まで", await tooLong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SystemRows_CannotBeRemoved_EvenWithACraftedRequest()
    {
        var admin = await AdminAsync();

        var withoutFolder = await PostKindsAsync(admin, Generic(), (null, "A", "種類A", "a"));
        var empty = await admin.PostFormAsync(SettingsPage, SettingsPage, []);

        Assert.Contains("「フォルダ」の行は必須", await withoutFolder.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("必須", await empty.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("現在は標準の設定を使っています", await admin.GetStringAsync(SettingsPage, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidSave_AfterAValidOne_KeepsThePreviousSettings()
    {
        var admin = await AdminAsync();
        await SeedFilesAsync(admin, "app.log");
        await PostKindsAsync(admin, Folder(), Generic(), ("log", "📜", "ログ", "log"));

        await PostKindsAsync(admin, Folder(), Generic(), (null, "A", "A", "x"), (null, "B", "B", "x"));

        Assert.StartsWith("log|ログ|", IconOf(await admin.GetStringAsync("/", TestContext.Current.CancellationToken), "app.log"));
    }

    // ---- 安全性 ----

    [Fact]
    public async Task LabelsAndIcons_AreHtmlEncodedEverywhere()
    {
        var admin = await AdminAsync();
        await SeedFilesAsync(admin, "x.evil");

        var response = await PostKindsAsync(admin, Folder(), Generic(),
            (null, "<i>", "<script>alert(1)</script>", "evil"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        foreach (var page in new[] { await admin.GetStringAsync("/", TestContext.Current.CancellationToken), await admin.GetStringAsync(SettingsPage, TestContext.Current.CancellationToken) })
        {
            Assert.DoesNotContain("<script>alert(1)</script>", page);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page);
            Assert.DoesNotContain("<i></span>", page);
        }
    }

    // ---- 操作ログ ----

    [Fact]
    public async Task SavingAndResetting_AreWrittenToTheOperationLog()
    {
        var admin = await AdminAsync();
        await PostKindsAsync(admin, Folder(), Generic(), (null, "A", "種類A", "a"));
        await admin.PostFormAsync("/Settings/ResetFileKinds", SettingsPage, []);

        var logs = await admin.GetStringAsync("/Logs?Operation=AdminChange", TestContext.Current.CancellationToken);

        Assert.Contains("ファイルの種類の設定を更新（3 件）", logs);
        Assert.Contains("ファイルの種類の設定を既定に戻した", logs);
    }

    [Fact]
    public async Task RemoveButton_IsAnAccessibleIconButton_OnlyOnEditableRows()
    {
        var admin = await AdminAsync();

        var html = await admin.GetStringAsync(SettingsPage, TestContext.Current.CancellationToken);

        // 文字ではなくアイコン（SVG）のボタン。読み上げ用のラベルとツールチップを持つ。
        var buttons = Regex.Matches(html, "<button type=\"button\" class=\"icon-button\" data-remove-kind[^>]*>.*?</button>", RegexOptions.Singleline);
        Assert.NotEmpty(buttons);
        Assert.All(buttons, button =>
        {
            Assert.Contains("aria-label=\"この種類を削除\"", button.Value);
            Assert.Contains("title=\"この種類を削除\"", button.Value);
            Assert.Contains("<svg", button.Value);
            Assert.Contains("aria-hidden=\"true\"", button.Value);
            Assert.DoesNotMatch(">\\s*削除\\s*<", button.Value);   // 折り返して崩れる文字は使わない
        });

        // 削除できるのは、標準の種類（フォルダと汎用のファイルを除く）の行と、追加用のテンプレート行。
        var editableDefaults = NowFound.Application.FileKinds.DefaultFileKinds.Definitions.Count(d => !d.IsSystem);
        Assert.Equal(editableDefaults + 1, buttons.Count);
        Assert.DoesNotContain(">削除</button>", html);
    }

    [Fact]
    public async Task EditorScript_IsPartOfTheSite_AndReferencesTheRowsTheViewRenders()
    {
        var admin = await AdminAsync();

        var html = await admin.GetStringAsync(SettingsPage, TestContext.Current.CancellationToken);
        var script = await admin.GetStringAsync("/js/site.js", TestContext.Current.CancellationToken);

        // 画面の要素の ID と、スクリプトが探す ID が食い違っていないこと。
        foreach (var id in new[] { "kind-rows", "kind-row-template", "add-kind" })
        {
            Assert.Contains($"id=\"{id}\"", html);
            Assert.Contains($"getElementById('{id}')", script);
        }

        Assert.Contains("data-kind-row", html);
        Assert.Contains("data-remove-kind", html);
        Assert.Contains("__INDEX__", html);   // 追加用のテンプレート行
    }
}
