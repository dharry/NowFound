using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace NowFound.IntegrationTests;

public sealed class AdminAndSearchTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task NormalUser_CannotOpenAdminPages()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        var created = await admin.PostFormAsync("/Users/Create", "/Users/Create",
        [
            new("Email", "user@example.com"),
            new("Password", "Passw0rd!"),
            new("RequirePasswordChange", "false"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "Passw0rd!");

        foreach (var url in new[] { "/Users", "/Folders", "/Groups", "/Settings" })
        {
            var response = await user.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("AccessDenied", response.Headers.Location!.OriginalString);
        }

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/Account/Preferences", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Admin_CanRegisterFolderGroupAndSettings()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        var root = Path.GetFullPath(Path.GetTempPath());

        var folder = await admin.PostFormAsync("/Folders/Save", "/Folders/Create",
        [
            new("Name", "Temp"),
            new("Path", root),
            new("ExcludeHidden", "true"),
            new("ExcludePatterns", "*.tmp\r\nnode_modules"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, folder.StatusCode);
        var folders = await admin.GetStringAsync("/Folders", TestContext.Current.CancellationToken);
        Assert.Contains("Temp", folders);

        var badFolder = await admin.PostFormAsync("/Folders/Save", "/Folders/Create",
            [new("Name", "Bad"), new("Path", "relative")]);
        Assert.Equal(HttpStatusCode.OK, badFolder.StatusCode);
        Assert.Contains("絶対パス", await badFolder.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var group = await admin.PostFormAsync("/Groups/Save", "/Groups/Create", [new("Name", "Team")]);
        Assert.Equal(HttpStatusCode.Redirect, group.StatusCode);
        Assert.Contains("Team", await admin.GetStringAsync("/Groups", TestContext.Current.CancellationToken));

        var size = await admin.PostFormAsync("/Settings", "/Settings", [new("PageSize", "25")]);
        Assert.Equal(HttpStatusCode.Redirect, size.StatusCode);
        Assert.Contains("value=\"25\"", await admin.GetStringAsync("/Settings", TestContext.Current.CancellationToken));

        var tooBig = await admin.PostFormAsync("/Settings", "/Settings", [new("PageSize", "999999")]);
        Assert.Equal(HttpStatusCode.OK, tooBig.StatusCode);
    }

    [Fact]
    public async Task Forms_WithEmptyFields_DoNotCrash()
    {
        // ブラウザは未入力欄を空文字で送信する（フィールド自体は省略されない）。
        var anon = _factory.CreateBrowserLikeClient();
        var login = await anon.PostFormAsync("/Account/Login", "/Account/Login", [new("Email", ""), new("Password", "")]);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        var root = Path.GetFullPath(Path.GetTempPath());

        var folder = await admin.PostFormAsync("/Folders/Save", "/Folders/Create",
            [new("Id", "0"), new("Name", "Temp"), new("Path", root), new("ExcludeHidden", "true"), new("ExcludePatterns", "")]);
        Assert.Equal(HttpStatusCode.Redirect, folder.StatusCode);

        var emptyFolder = await admin.PostFormAsync("/Folders/Save", "/Folders/Create",
            [new("Name", ""), new("Path", ""), new("ExcludePatterns", "")]);
        Assert.Equal(HttpStatusCode.OK, emptyFolder.StatusCode);

        var group = await admin.PostFormAsync("/Groups/Save", "/Groups/Create",
            [new("Id", "0"), new("Name", "Team"), new("Description", "")]);
        Assert.Equal(HttpStatusCode.Redirect, group.StatusCode);

        var user = await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", ""), new("Password", "")]);
        Assert.Equal(HttpStatusCode.OK, user.StatusCode);

        var password = await admin.PostFormAsync("/Account/ChangePassword", "/Account/Preferences",
            [new("CurrentPassword", ""), new("NewPassword", ""), new("ConfirmPassword", "")]);
        Assert.Equal(HttpStatusCode.OK, password.StatusCode);

        var search = await admin.GetAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=&Operator=And&FolderPath=", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        var logs = await admin.GetAsync("/Logs?From=&To=&Operation=&User=&Page=1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
    }

    [Fact]
    public async Task SearchPage_RendersForAdmin_AndSubFoldersReturnsJson()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create",
            [new("Name", "Temp"), new("Path", Path.GetFullPath(Path.GetTempPath()))]);

        var html = await admin.GetStringAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=abc&Operator=Or", TestContext.Current.CancellationToken);
        Assert.Contains("検索対象フォルダ", html);
        Assert.Contains("Temp", html);
        Assert.Contains("value=\"abc\"", html);

        var response = await admin.GetAsync("/Home/SubFolders?folderId=1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
    }

    [Fact]
    public async Task SearchPage_ShowsSortLinksCopyButtonAndPathFilter()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "Temp"), new("Path", root)]);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<NowFound.Infrastructure.Data.FilesDbContext>();
            var now = DateTime.UtcNow;
            var directory = new NowFound.Domain.Entities.DirectoryEntry { SearchFolderId = 1, Path = root };
            files.Directories.Add(directory);
            await files.SaveChangesAsync(TestContext.Current.CancellationToken);
            files.Files.AddRange(
                new NowFound.Domain.Entities.FileEntry
                {
                    SearchFolderId = 1, DirectoryId = directory.Id, Name = "alpha.txt",
                    Extension = "txt", Size = 10, ModifiedAt = now, CreatedAt = now,
                },
                new NowFound.Domain.Entities.FileEntry
                {
                    SearchFolderId = 1, DirectoryId = directory.Id, Name = "beta.txt",
                    Extension = "txt", Size = 20, ModifiedAt = now, CreatedAt = now,
                });
            await files.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var html = await admin.GetStringAsync("/?Sort=Size&Desc=true", TestContext.Current.CancellationToken);

        Assert.True(html.IndexOf("beta.txt", StringComparison.Ordinal) < html.IndexOf("alpha.txt", StringComparison.Ordinal));
        Assert.Contains("aria-sort=\"descending\"", html);
        Assert.Contains("Sort=Name", html);
        Assert.Contains("data-copy=\"", html);
        Assert.Contains("このフォルダ配下に絞り込む", html);
        Assert.Contains("data-col-toggle=\"owner\"", html);

        var scoped = await admin.GetStringAsync("/?FolderId=1&FolderPath=" + Uri.EscapeDataString(root), TestContext.Current.CancellationToken);
        Assert.Contains("alpha.txt", scoped);
        Assert.Contains("絞り込み:", scoped);
    }

    [Fact]
    public async Task SearchResults_ShowAnIconPerFileKind_AndAGenericOneForUnlistedTypes()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "Temp"), new("Path", root)]);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<NowFound.Infrastructure.Data.FilesDbContext>();
            var directory = new NowFound.Domain.Entities.DirectoryEntry { SearchFolderId = 1, Path = root };
            files.Directories.Add(directory);
            await files.SaveChangesAsync(TestContext.Current.CancellationToken);

            var now = DateTime.UtcNow;
            (string Name, string Extension, bool IsDirectory)[] entries =
            [
                ("notes.txt", "txt", false), ("disc.iso", "iso", false), ("setup.exe", "exe", false), ("core.dll", "dll", false),
                ("pack.zip", "zip", false), ("app.js", "js", false), ("clip.mp4", "mp4", false), ("photo.png", "png", false),
                ("song.mp3", "mp3", false), ("manual.pdf", "pdf", false), ("report.docx", "docx", false),
                ("sheet.ods", "ods", false), ("readme.md", "md", false), ("app.db", "db", false),
                ("app.slnx", "slnx", false), ("data.xml", "xml", false), ("setup.msi", "msi", false), ("tweak.reg", "reg", false),
                ("data.xyz", "xyz", false), ("subfolder", "", true),
            ];
            files.Files.AddRange(entries.Select(e => new NowFound.Domain.Entities.FileEntry
            {
                SearchFolderId = 1, DirectoryId = directory.Id, Name = e.Name, Extension = e.Extension,
                IsDirectory = e.IsDirectory, Size = e.IsDirectory ? null : 1, ModifiedAt = now, CreatedAt = now,
            }));
            await files.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var html = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);

        foreach (var (kind, label) in new[]
        {
            ("text", "テキストファイル"), ("iso", "ISOイメージ"), ("executable", "実行ファイル"), ("library", "ライブラリ"),
            ("archive", "圧縮アーカイブ"), ("code", "プログラム言語ファイル"), ("video", "動画"), ("image", "画像"),
            ("audio", "音楽"), ("pdf", "PDFファイル"), ("markdown", "マークダウン"), ("database", "データベース"),
            ("visualstudio", "Visual Studio ファイル"), ("xml", "XMLファイル"), ("installer", "インストーラ"),
            ("registry", "レジストリ"), ("folder", "フォルダ"), ("file", "ファイル"),
        })
        {
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(html, $"data-kind=\"{kind}\" title=\"{label}\""));
        }

        // docx と ods は、どちらも「オフィスファイル」（MS Office と LibreOffice）。
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(html, "data-kind=\"office\" title=\"オフィスファイル\""));

        // 一覧にない拡張子（xyz）は、汎用のファイルアイコン。アイコンのない行は無い。
        Assert.Equal(20, System.Text.RegularExpressions.Regex.Count(html, "data-kind=\""));
        Assert.DoesNotContain("<span class=\"file-icon\" aria-hidden=\"true\"></span>", html);
        Assert.Contains("data.xyz", html);
    }

    [Fact]
    public async Task SearchPage_DetailPanelIsOpenOnlyForMultipleConditions()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var simple = await admin.GetStringAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=abc", TestContext.Current.CancellationToken);
        var detailed = await admin.GetStringAsync(
            "/?Conditions[0].Field=FileName&Conditions[0].Text=abc&Conditions[1].Field=Extension&Conditions[1].Text=txt&Operator=Or", TestContext.Current.CancellationToken);

        Assert.Contains("id=\"detail-panel\" hidden", simple);
        Assert.DoesNotContain("id=\"detail-panel\" hidden", detailed);
        Assert.Contains("name=\"Conditions[1].Text\"", detailed);
    }

    [Fact]
    public async Task Layout_HasNavToggle_OnlyWhenSignedIn()
    {
        var client = _factory.CreateBrowserLikeClient();
        Assert.DoesNotContain("id=\"nav-toggle\"", await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken));

        await client.LoginAsAdminAsync();
        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);
        Assert.Contains("id=\"nav-toggle\"", html);
        Assert.Contains("aria-controls=\"global-nav\"", html);
        Assert.Contains("id=\"global-nav\"", html);
    }

    [Fact]
    public async Task Preferences_SavesUserPageSize()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var saved = await admin.PostFormAsync("/Account/Preferences", "/Account/Preferences", [new("PageSize", "42"), new("Theme", "light")]);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("value=\"42\"", await admin.GetStringAsync("/Account/Preferences", TestContext.Current.CancellationToken));

        var invalid = await admin.PostFormAsync("/Account/Preferences", "/Account/Preferences", [new("PageSize", "0"), new("Theme", "light")]);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
    }

    [Fact]
    public async Task SearchPage_WhenConditionsOrTextExceedsLimits_ShowsWarningAndDoesNotExecuteSearch()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        // 1. 条件数が10個を超える場合（11個）
        var tooManyQuery = string.Join("&", Enumerable.Range(0, 11).Select(i => $"Conditions[{i}].Field=FileName&Conditions[{i}].Text=test{i}"));
        var responseTooMany = await admin.GetStringAsync("/?" + tooManyQuery, TestContext.Current.CancellationToken);
        Assert.Contains("検索条件は 10 個までです。", responseTooMany);

        // 2. 検索テキストが200文字を超える場合（201文字）
        var longText = new string('a', 201);
        var responseLong = await admin.GetStringAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=" + longText, TestContext.Current.CancellationToken);
        Assert.Contains("キーワードは 200 文字までです。", responseLong);

        // 3. フォルダパスが1000文字を超える場合（1001文字）
        var longPath = new string('a', 1001);
        var responseLongPath = await admin.GetStringAsync("/?FolderPath=" + longPath, TestContext.Current.CancellationToken);
        Assert.Contains("フォルダパスが長すぎます。", responseLongPath);
    }

    [Fact]
    public async Task SearchPage_RendersClearKeywordButton()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        // 1. キーワード入力時: プライマリ検索欄のクリアボタンが表示（hidden属性なし）
        var withKeyword = await admin.GetStringAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=sample", TestContext.Current.CancellationToken);
        Assert.Contains("data-clear-text", withKeyword);
        Assert.Matches(@"<div[^>]*id=""primary-condition""[\s\S]*?<button\s+type=""button""\s+class=""clear-input""\s+data-clear-text[^>]*aria-label=""キーワードをクリア""[^>]*>\s*×\s*</button>", withKeyword);
        Assert.Matches(@"<div[^>]*id=""primary-condition""[\s\S]*?<button(?![^>]*hidden)[^>]*data-clear-text", withKeyword);

        // 2. キーワード未入力時: プライマリ検索欄のクリアボタンに hidden 属性が付与されていること
        var emptyKeyword = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);
        Assert.Contains("data-clear-text", emptyKeyword);
        Assert.Matches(@"<div[^>]*id=""primary-condition""[\s\S]*?<button[^>]*data-clear-text[^>]*hidden", emptyKeyword);
    }
}
