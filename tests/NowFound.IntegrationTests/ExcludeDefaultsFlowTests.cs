using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Settings;

namespace NowFound.IntegrationTests;

public sealed class ExcludeDefaultsFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        return admin;
    }

    [Fact]
    public async Task NewFolderForm_IsPrefilledWithBuiltInDefaults_ButSavedFoldersAreNotTouched()
    {
        var admin = await AdminAsync();
        var ct = TestContext.Current.CancellationToken;

        var html = WebUtility.HtmlDecode(await admin.GetStringAsync("/Folders/Create", ct));
        foreach (var pattern in new[] { ".DS_Store", "__MACOSX/", "Thumbs.db", "$RECYCLE.BIN/", "~$*", "#*#", ".nfs*" })
        {
            Assert.Contains(pattern, html);
        }

        // 既定を変えても、登録済みのフォルダの除外パターンは変わらない。
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create",
            [new("Name", "T"), new("Path", root), new("ExcludePatterns", "only-this")]);
        await admin.PostFormAsync("/Settings/ExcludeDefaults", "/Settings/ExcludeDefaults", [new("Patterns", "*.bak")]);

        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAdminSettingsService>();
        var folder = (await service.ListFoldersAsync(ct)).Single();
        var edit = await service.GetFolderAsync(folder.Id, ct);
        Assert.Equal(["only-this"], edit!.ExcludePatterns);
    }

    [Fact]
    public async Task AdminDefaults_AreShownOnTheNewFolderForm_AndCanBeReset()
    {
        var admin = await AdminAsync();
        var ct = TestContext.Current.CancellationToken;

        var save = await admin.PostFormAsync("/Settings/ExcludeDefaults", "/Settings/ExcludeDefaults",
            [new("Patterns", "*.bak\r\nbuild/\r\n")]);
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        var form = WebUtility.HtmlDecode(await admin.GetStringAsync("/Folders/Create", ct));
        Assert.Contains("*.bak", form);
        Assert.Contains("build/", form);
        Assert.DoesNotContain(".DS_Store", form);

        var reset = await admin.PostFormAsync("/Settings/ResetExcludeDefaults", "/Settings/ExcludeDefaults", []);
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Contains(".DS_Store", WebUtility.HtmlDecode(await admin.GetStringAsync("/Folders/Create", ct)));
    }

    [Fact]
    public async Task InvalidPattern_ShowsError_AndKeepsTheInput()
    {
        var admin = await AdminAsync();

        var response = await admin.PostFormAsync("/Settings/ExcludeDefaults", "/Settings/ExcludeDefaults",
            [new("Patterns", "good\r\na/b")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("名前だけを指定", html);
        Assert.Contains("a/b", html);
    }

    [Fact]
    public async Task Anonymous_CannotOpenTheSettingsPage()
    {
        var anonymous = _factory.CreateBrowserLikeClient();
        var response = await anonymous.GetAsync("/Settings/ExcludeDefaults", TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
