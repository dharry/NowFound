using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Settings;

namespace NowFound.IntegrationTests;

public sealed partial class SettingsTransferFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        return admin;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string action, string content, string fileName = "settings.json")
    {
        var html = await client.GetStringAsync("/SettingsTransfer", TestContext.Current.CancellationToken);
        var token = TokenRegex().Match(html).Groups[1].Value;
        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName },
        };
        return await client.PostAsync($"/SettingsTransfer/{action}", form, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pages_AreForbiddenForNormalUsers_AndRedirectAnonymousUsersToLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        var created = await admin.PostFormAsync("/Users/Create", "/Users/Create",
        [
            new("Email", "user@example.com"),
            new("Password", "Passw0rd!"),
            new("RequirePasswordChange", "false"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "Passw0rd!");
        var anonymous = _factory.CreateBrowserLikeClient();

        foreach (var url in new[] { "/SettingsTransfer", "/SettingsTransfer/ExportFolders", "/SettingsTransfer/ExportSystemSettings", "/SettingsTransfer/ExportFileKinds" })
        {
            var forbidden = await user.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.Redirect, forbidden.StatusCode);
            Assert.Contains("AccessDenied", forbidden.Headers.Location!.OriginalString);

            var unauthenticated = await anonymous.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.Redirect, unauthenticated.StatusCode);
            Assert.Contains("Login", unauthenticated.Headers.Location!.OriginalString);
        }

        var upload = await UploadAsync(admin, "ImportSystemSettings", "{}");
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);

        // 一般ユーザーは取り込みもできない（トークンはユーザー自身のページから取れないため、管理者のトークンで試す）。
        var html = await admin.GetStringAsync("/SettingsTransfer", ct);
        var token = TokenRegex().Match(html).Groups[1].Value;
        var denied = await user.PostAsync("/SettingsTransfer/ImportFolders", new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("""{"format":"nowfound.folders","version":1,"folders":[]}"""), "file", "f.json" },
        }, ct);
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
    }

    [Fact]
    public async Task SettingsPage_LinksToTheTransferPage()
    {
        var admin = await AdminAsync();

        var html = await admin.GetStringAsync("/Settings", TestContext.Current.CancellationToken);

        Assert.Contains("/SettingsTransfer", html);
    }

    [Fact]
    public async Task Admin_CanExportFoldersAsAJsonDownload()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "資料"), new("Path", root)]);

        var response = await admin.GetAsync("/SettingsTransfer/ExportFolders", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("nowfound-folders-", response.Content.Headers.ContentDisposition.FileName);
        var json = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("資料", json);
        Assert.Contains("nowfound.folders", json);
    }

    [Fact]
    public async Task Admin_CanImportFolders_AndTheOperationIsLogged()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var json = $$"""{"format":"nowfound.folders","version":1,"folders":[{"name":"取込","path":{{System.Text.Json.JsonSerializer.Serialize(root)}},"excludePatterns":["*.tmp"]}]}""";

        var response = await UploadAsync(admin, "ImportFolders", json);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAdminSettingsService>();
        var folder = (await settings.ListFoldersAsync(ct)).Single();
        Assert.Equal("取込", folder.Name);
        Assert.Equal(["*.tmp"], (await settings.GetFolderAsync(folder.Id, ct))!.ExcludePatterns);

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/SettingsTransfer", ct));
        Assert.Contains("追加 1 件", page);
        var logs = WebUtility.HtmlDecode(await admin.GetStringAsync("/Logs", ct));
        Assert.Contains("検索対象フォルダをインポート", logs);
    }

    [Fact]
    public async Task Admin_SeesTheError_WhenTheImportFileIsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();

        var response = await UploadAsync(admin, "ImportFolders", "not json");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/SettingsTransfer", ct));
        Assert.Contains("JSON として読み込めません", page);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IAdminSettingsService>().ListFoldersAsync(ct));
    }

    [Fact]
    public async Task Admin_CanRoundTripTheSystemSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        await admin.PostFormAsync("/Settings/Index", "/Settings",
            [new("PageSize", "321"), new("LogPageSize", "100"), new("AdminListPageSize", "50"),
             new("IncrementalEnabled", "true"), new("IncrementalMinChars", "2"), new("IncrementalDelayMs", "100")]);
        var exported = await admin.GetStringAsync("/SettingsTransfer/ExportSystemSettings", ct);
        Assert.Contains("\"pageSize\": 321", exported);

        await admin.PostFormAsync("/Settings/Index", "/Settings",
            [new("PageSize", "100"), new("LogPageSize", "100"), new("AdminListPageSize", "50"),
             new("IncrementalEnabled", "true"), new("IncrementalMinChars", "2"), new("IncrementalDelayMs", "100")]);
        var response = await UploadAsync(admin, "ImportSystemSettings", exported);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Equal(321, await scope.ServiceProvider.GetRequiredService<IAdminSettingsService>().GetDefaultPageSizeAsync(ct));
    }

    [Fact]
    public async Task ImportingABindUser_ShowsAMessageToSetThePasswordAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        var json = """{"format":"nowfound.system-settings","version":1,"activeDirectory":{"server":"dc.corp.example.com","port":389,"domain":"corp.example.com","bindUser":"svc-read"}}""";

        var response = await UploadAsync(admin, "ImportSystemSettings", json);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/SettingsTransfer", ct));
        Assert.Contains("システム設定を取り込みました", page);
        Assert.Contains("確認用アカウントのパスワードを再設定してください", page);
    }

    [Fact]
    public async Task Admin_CanRoundTripTheFileKinds_AndTheOperationIsLogged()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        var json = """{"format":"nowfound.file-kinds","version":1,"customized":true,"kinds":[{"key":"folder","label":"フォルダ","icon":"📁","extensions":[]},{"key":"file","label":"ファイル","icon":"📄","extensions":[]},{"key":"cad","label":"図面","icon":"📐","extensions":["dwg"]}]}""";

        var response = await UploadAsync(admin, "ImportFileKinds", json);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var exported = await admin.GetStringAsync("/SettingsTransfer/ExportFileKinds", ct);
        Assert.Contains("図面", exported);
        Assert.Contains("\"dwg\"", exported);
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/SettingsTransfer", ct));
        Assert.Contains("ファイルの種類の設定を取り込みました", page);
        var logs = WebUtility.HtmlDecode(await admin.GetStringAsync("/Logs", ct));
        Assert.Contains("ファイルの種類の設定をインポート", logs);
    }

    [Fact]
    public async Task Admin_SeesTheError_WhenTheFileKindsFileIsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();

        var response = await UploadAsync(admin, "ImportFileKinds",
            """{"format":"nowfound.file-kinds","version":1,"customized":true,"kinds":[]}""");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/SettingsTransfer", ct));
        Assert.DoesNotContain("ファイルの種類の設定を取り込みました", page);
        Assert.Contains("validation-summary", page);
    }

    [Fact]
    public async Task Import_WithoutAFile_ShowsAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        var html = await admin.GetStringAsync("/SettingsTransfer", ct);
        var token = TokenRegex().Match(html).Groups[1].Value;

        var response = await admin.PostAsync("/SettingsTransfer/ImportFolders",
            new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } }, ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/SettingsTransfer", ct));
        Assert.Contains("ファイルを選択してください", page);
    }

    [Fact]
    public async Task Import_RejectsAFileOverTheSizeLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        var big = new string(' ', SettingsTransferController_MaxFileBytes + 1);

        var response = await UploadAsync(admin, "ImportSystemSettings", big);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/SettingsTransfer", ct));
        Assert.Contains("大きすぎます", page);
    }

    private const int SettingsTransferController_MaxFileBytes = 1024 * 1024;
}
