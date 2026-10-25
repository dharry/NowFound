using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Logging;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.IntegrationTests;

/// <summary>検索結果のエクスポート（CSV / TSV）。権限・形式・文字コード・上限・操作ログ。</summary>
public sealed class SearchExportFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static string Root => Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);

    private async Task<HttpClient> AdminWithFilesAsync(int fileCount = 3)
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "Temp"), new("Path", Root)]);
        // 表示件数を 1 にして、エクスポートがページ送りに左右されないことも確かめる。
        await admin.PostFormAsync("/Settings", "/Settings", [new("PageSize", "1")]);

        await using var scope = _factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var directory = new DirectoryEntry { SearchFolderId = 1, Path = Root };
        files.Directories.Add(directory);
        await files.SaveChangesAsync(TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        files.Files.AddRange(Enumerable.Range(1, fileCount).Select(i => new FileEntry
        {
            SearchFolderId = 1, DirectoryId = directory.Id, Name = i == 1 ? "=report,1.dat" : $"f{i}.dat", Extension = "dat", Size = i, ModifiedAt = now, CreatedAt = now,
        }));
        await files.SaveChangesAsync(TestContext.Current.CancellationToken);
        return admin;
    }

    private async Task<HttpClient> UserAsync()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserAdminService>()
                .CreateAsync(new CreateUserRequest("user@example.com", "Passw0rd!", false, false));
        }

        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "Passw0rd!");
        return user;
    }

    private static Task<HttpResponseMessage> SaveExportSettingsAsync(HttpClient admin, bool allowAll, string maxRows) =>
        admin.PostFormAsync("/Settings", "/Settings",
        [
            new("PageSize", "1"), new("LogPageSize", "100"), new("AdminListPageSize", "50"),
            new("ExportAllowAllUsers", allowAll ? "true" : "false"), new("ExportMaxRows", maxRows),
        ]);

    private static async Task<(HttpResponseMessage Response, byte[] Bytes)> GetBytesAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    private static string[] Lines(byte[] bytes) =>
        new UTF8Encoding(false).GetString(bytes[3..]).TrimEnd().Split("\r\n");

    [Fact]
    public async Task Admin_DownloadsAllRows_AsCsvWithBom_IgnoringThePageSize()
    {
        var admin = await AdminWithFilesAsync();

        var (response, bytes) = await GetBytesAsync(admin, "/Home/Export?FolderId=1&Sort=Name&format=Csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Matches(@"^nowfound-\d{8}-\d{6}\.csv$", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Lines(bytes);
        Assert.Equal(4, lines.Length);
        Assert.Contains(lines, l => l.StartsWith("f2.dat,"));
        Assert.Contains(lines, l => l.StartsWith("f3.dat,"));
    }

    [Fact]
    public async Task Csv_QuotesAndNeutralizesDangerousFileNames()
    {
        var admin = await AdminWithFilesAsync();

        var (_, bytes) = await GetBytesAsync(admin, "/Home/Export?FolderId=1&format=Csv");

        Assert.Contains(Lines(bytes), l => l.StartsWith("\"'=report,1.dat\","));
    }

    [Fact]
    public async Task Tsv_UsesTabs_AndShiftJisHasNoBom()
    {
        var admin = await AdminWithFilesAsync();

        var (tsv, tsvBytes) = await GetBytesAsync(admin, "/Home/Export?FolderId=1&format=Tsv");
        var (sjis, sjisBytes) = await GetBytesAsync(admin, "/Home/Export?FolderId=1&format=Csv&encoding=ShiftJis");

        Assert.Equal("text/tab-separated-values", tsv.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".tsv", tsv.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal(9, Lines(tsvBytes)[0].Split('\t').Length);
        Assert.Equal("shift_jis", sjis.Content.Headers.ContentType!.CharSet);
        Assert.NotEqual(0xEF, sjisBytes[0]);
        Assert.Contains("f2.dat", Encoding.ASCII.GetString(sjisBytes));
    }

    [Fact]
    public async Task Export_StopsAtTheConfiguredMaxRows()
    {
        var admin = await AdminWithFilesAsync();
        await SaveExportSettingsAsync(admin, allowAll: false, maxRows: "2");

        var (_, bytes) = await GetBytesAsync(admin, "/Home/Export?FolderId=1&format=Csv");

        Assert.Equal(3, Lines(bytes).Length);
    }

    [Fact]
    public async Task Users_AreRefusedByDefault_AndAllowedWhenTheAdminPermitsIt()
    {
        var admin = await AdminWithFilesAsync();
        var user = await UserAsync();

        var denied = await user.GetAsync("/Home/Export?format=Csv", TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);

        await SaveExportSettingsAsync(admin, allowAll: true, maxRows: "1000");
        var allowed = await user.GetAsync("/Home/Export?format=Csv", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Toolbar_ShowsTheExportForm_OnlyToThoseAllowed()
    {
        var admin = await AdminWithFilesAsync();
        var user = await UserAsync();
        var ct = TestContext.Current.CancellationToken;

        var adminHtml = await admin.GetStringAsync("/?FolderId=1", ct);
        Assert.Contains("class=\"export-form\"", adminHtml);
        Assert.Contains("name=\"format\"", adminHtml);
        Assert.Contains("name=\"encoding\"", adminHtml);
        Assert.Contains("name=\"FolderId\" value=\"1\"", adminHtml);

        Assert.DoesNotContain("class=\"export-form\"", await user.GetStringAsync("/", ct));

        await SaveExportSettingsAsync(admin, allowAll: true, maxRows: "1000");
        Assert.Contains("class=\"export-form\"", await user.GetStringAsync("/", ct));
    }

    [Fact]
    public async Task InvalidQuery_IsRejectedWith400()
    {
        var admin = await AdminWithFilesAsync();
        var query = string.Join('&', Enumerable.Range(0, 11).Select(i => $"Conditions[{i}].Field=FileName&Conditions[{i}].Text=x{i}"));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/Home/Export?{query}", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/Home/Export?format=Xml", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Export_IsWrittenToTheOperationLog()
    {
        var admin = await AdminWithFilesAsync();
        await GetBytesAsync(admin, "/Home/Export?FolderId=1&format=Tsv");

        await using var scope = _factory.Services.CreateAsyncScope();
        var logs = await scope.ServiceProvider.GetRequiredService<IOperationLogService>()
            .SearchAsync(new OperationLogQuery(null, null, OperationType.Export, null, 1, 50), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logs.Items);
        Assert.Equal(OperationType.Export, entry.Operation);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10000001")]
    public async Task Settings_RejectAnOutOfRangeMaxRows(string rows)
    {
        var admin = await AdminWithFilesAsync();

        var response = await SaveExportSettingsAsync(admin, allowAll: true, maxRows: rows);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Equal(ExportSettings.Default, await scope.ServiceProvider.GetRequiredService<IAdminSettingsService>().GetExportAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SettingsPage_ShowsTheExportSection()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var html = await admin.GetStringAsync("/Settings", TestContext.Current.CancellationToken);

        Assert.Contains("name=\"ExportAllowAllUsers\"", html);
        Assert.Contains("name=\"ExportMaxRows\"", html);
        Assert.Contains("value=\"1000000\"", html);
    }
}
