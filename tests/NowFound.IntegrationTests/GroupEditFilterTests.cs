namespace NowFound.IntegrationTests;

public sealed class GroupEditFilterTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GroupForm_HasFiltersAndThreeColumnLists_ForMembersAndFolders()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var html = await admin.GetStringAsync("/Groups/Create", TestContext.Current.CancellationToken);

        Assert.Matches("<input[^>]*type=\"search\"[^>]*data-check-filter=\"members\"", html);
        Assert.Matches("<input[^>]*type=\"search\"[^>]*data-check-filter=\"folders\"", html);
        Assert.Contains("data-check-list=\"members\"", html);
        Assert.Contains("data-check-list=\"folders\"", html);
    }

    [Fact]
    public async Task SiteAssets_ImplementPartialMatchFilteringAndColumns()
    {
        var client = _factory.CreateBrowserLikeClient();
        var ct = TestContext.Current.CancellationToken;

        var css = await client.GetStringAsync("/css/site.css", ct);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr));", css);
        Assert.Contains(".check[hidden] { display: none; }", css);

        var js = await client.GetStringAsync("/js/site.js", ct);
        Assert.Contains("[data-check-filter]", js);
        Assert.Contains(".includes(keyword)", js);
    }
}
