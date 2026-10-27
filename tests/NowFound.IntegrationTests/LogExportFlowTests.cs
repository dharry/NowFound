using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Logging;
using NowFound.Application.Users;
using NowFound.Domain.Entities;

namespace NowFound.IntegrationTests;

/// <summary>操作ログのエクスポート（CSV / TSV）。管理者のみ。</summary>
public sealed class LogExportFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
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

    private static async Task<(HttpResponseMessage Response, byte[] Bytes)> GetBytesAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Admin_DownloadsLogsAsCsvWithBom_AndFiltersApply()
    {
        var admin = await AdminAsync();

        var (response, bytes) = await GetBytesAsync(admin, "/Logs/Export?Operation=Login&format=Csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Matches(@"^nowfound-log-\d{8}-\d{6}\.csv$", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = new UTF8Encoding(false).GetString(bytes[3..]).TrimEnd().Split("\r\n");
        Assert.True(lines.Length >= 2, "ログイン操作が 1 件以上出力される");
        Assert.All(lines.Skip(1), l => Assert.Contains("admin@nowfound.invalid", l));
    }

    [Fact]
    public async Task Tsv_UsesTabs_AndShiftJisHasNoBom()
    {
        var admin = await AdminAsync();

        var (tsv, tsvBytes) = await GetBytesAsync(admin, "/Logs/Export?format=Tsv");
        var (sjis, sjisBytes) = await GetBytesAsync(admin, "/Logs/Export?format=Csv&encoding=ShiftJis");

        Assert.Equal("text/tab-separated-values", tsv.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".tsv", tsv.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal(5, new UTF8Encoding(false).GetString(tsvBytes[3..]).Split("\r\n")[0].Split('\t').Length);
        Assert.Equal("shift_jis", sjis.Content.Headers.ContentType!.CharSet);
        Assert.NotEqual(0xEF, sjisBytes[0]);
    }

    [Fact]
    public async Task NonAdmins_AreRefused_AndAnonymousIsNotServed()
    {
        await AdminAsync();
        var user = await UserAsync();

        var denied = await user.GetAsync("/Logs/Export?format=Csv", TestContext.Current.CancellationToken);
        var anonymous = await _factory.CreateBrowserLikeClient().GetAsync("/Logs/Export?format=Csv", TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
    }

    [Fact]
    public async Task InvalidFormat_IsRejected()
    {
        var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/Logs/Export?format=Xml", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/Logs/Export?encoding=Latin1", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task LogsPage_ShowsTheExportForm_WithTheCurrentFilter()
    {
        var admin = await AdminAsync();

        var html = await admin.GetStringAsync("/Logs?Operation=Login&User=admin", TestContext.Current.CancellationToken);

        Assert.Contains("class=\"export-form\"", html);
        Assert.Contains("action=\"/Logs/Export\"", html);
        Assert.Contains("name=\"Operation\" value=\"Login\"", html);
        Assert.Contains("name=\"User\" value=\"admin\"", html);
        Assert.DoesNotContain("Logs.Export.", html);
    }

    [Fact]
    public async Task Export_IsWrittenToTheOperationLog()
    {
        var admin = await AdminAsync();
        await GetBytesAsync(admin, "/Logs/Export?format=Tsv");

        await using var scope = _factory.Services.CreateAsyncScope();
        var page = await scope.ServiceProvider.GetRequiredService<IOperationLogService>()
            .SearchAsync(new OperationLogQuery(null, null, OperationType.Export, null, 1, 50), TestContext.Current.CancellationToken);

        var entry = Assert.Single(page.Items);
        Assert.Equal("Log.LogExport.Done", entry.DetailKey);
        Assert.Equal("TSV", entry.DetailArgs![0]);
    }
}
