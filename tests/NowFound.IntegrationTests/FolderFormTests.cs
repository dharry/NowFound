using System.Net;

namespace NowFound.IntegrationTests;

public sealed class FolderFormTests : IDisposable
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
    public async Task FolderForm_ShowsThePathBeforeTheName_AndWiresTheAutoFill()
    {
        var admin = await AdminAsync();

        var html = await admin.GetStringAsync("/Folders/Create", TestContext.Current.CancellationToken);

        var path = html.IndexOf("data-folder-path", StringComparison.Ordinal);
        var name = html.IndexOf("data-folder-name", StringComparison.Ordinal);
        Assert.True(path > 0, "パスの入力欄に data-folder-path がある");
        Assert.True(name > path, "パスの入力欄が表示名より前にある");
        Assert.True(
            html.IndexOf("name=\"Path\"", StringComparison.Ordinal) < html.IndexOf("name=\"Name\"", StringComparison.Ordinal),
            "フォームの順序がパス、表示名の順");
    }

    [Fact]
    public async Task SiteScript_FillsTheNameFromTheLastSegmentOfThePath()
    {
        var admin = await AdminAsync();

        var script = await admin.GetStringAsync("/js/site.js", TestContext.Current.CancellationToken);

        Assert.Contains("data-folder-path", script);
        Assert.Contains("data-folder-name", script);
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData("//server/share")]
    public async Task Save_WithAUncPath_ShowsAnError(string path)
    {
        var admin = await AdminAsync();

        var response = await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "共有"), new("Path", path)]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("UNC パス", html);
    }
}
