namespace NowFound.IntegrationTests;

/// <summary>管理画面の一覧の列（順序・並べ替え・表示切り替え）。</summary>
public sealed class FolderListColumnsTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task FolderList_ShowsShareColumnBetweenPathAndExcludeCount_AndEveryColumnIsSortable()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var page = await admin.GetStringAsync("/Folders", TestContext.Current.CancellationToken);

        var path = page.IndexOf("<th data-col=\"path\"", StringComparison.Ordinal);
        var share = page.IndexOf("<th data-col=\"share\"", StringComparison.Ordinal);
        var excludes = page.IndexOf("<th data-col=\"excludes\"", StringComparison.Ordinal);
        var groups = page.IndexOf("<th data-col=\"groups\"", StringComparison.Ordinal);
        Assert.True(path >= 0 && path < share && share < excludes && excludes < groups, "列の順序: パス → 共有フォルダ → 除外パターン数 → 割り当てグループ数");
        foreach (var sort in new[] { "name", "path", "share", "excludes", "groups" })
        {
            Assert.Contains($"Sort={sort}", page);
        }
    }

    [Fact]
    public async Task FolderList_HasColumnToggles_ForEveryColumnExceptName()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var page = await admin.GetStringAsync("/Folders", TestContext.Current.CancellationToken);

        Assert.Contains("<table id=\"folder-table\"", page);
        foreach (var key in new[] { "path", "share", "excludes", "groups" })
        {
            Assert.Contains($"data-col-table=\"folder-table\" data-col-toggle=\"{key}\"", page);
        }

        Assert.DoesNotContain("data-col-toggle=\"name\"", page);
    }

    [Theory]
    [InlineData("/Users", "user-table", new[] { "role", "mustchange", "status", "expires" })]
    [InlineData("/Groups", "group-table", new[] { "description", "members", "folders" })]
    [InlineData("/Logs", "log-table", new[] { "operation", "user", "detail", "ip" })]
    public async Task AdminLists_HaveColumnToggles_AndTheTableMatchesThem(string url, string tableId, string[] keys)
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var page = await admin.GetStringAsync(url, TestContext.Current.CancellationToken);

        Assert.Contains($"<table id=\"{tableId}\"", page);
        foreach (var key in keys)
        {
            Assert.Contains($"data-col-table=\"{tableId}\" data-col-toggle=\"{key}\"", page);
            Assert.Contains($"<th data-col=\"{key}\"", page);
        }
    }

    [Fact]
    public async Task SearchResults_SummaryAndCopyStatus_HaveAriaLivePolite()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();

        var page = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("class=\"summary\" aria-live=\"polite\"", page);
        Assert.Contains("id=\"copy-status\" role=\"status\" aria-live=\"polite\"", page);
    }

    [Fact]
    public async Task SiteAssets_ImplementCssClassBasedColumnHiding()
    {
        var client = _factory.CreateBrowserLikeClient();

        var css = await client.GetStringAsync("/css/site.css", TestContext.Current.CancellationToken);
        Assert.Contains("table.hide-col-size [data-col=\"size\"]", css);
        Assert.Contains("display: none !important;", css);

        var js = await client.GetStringAsync("/js/site.js", TestContext.Current.CancellationToken);
        Assert.Contains("table.classList.toggle(`hide-col-${col}`, isHidden)", js);
        Assert.DoesNotContain("cell.hidden = hidden.has", js);
    }

    [Fact]
    public async Task SiteJs_PersistsAndRestoresFolderTreeState()
    {
        var client = _factory.CreateBrowserLikeClient();

        var js = await client.GetStringAsync("/js/site.js", TestContext.Current.CancellationToken);

        Assert.Contains("nowfound.folderTree.expanded", js);
        Assert.Contains("nowfound.folderTree.scroll", js);
        Assert.Contains("restoreTree();", js);
        Assert.Contains("pagehide", js);
    }

    [Fact]
    public async Task SiteCss_FolderTreeWidthFitsContentUpToOneThirdWithHorizontalScroll()
    {
        var client = _factory.CreateBrowserLikeClient();

        var css = await client.GetStringAsync("/css/site.css", TestContext.Current.CancellationToken);

        Assert.Contains("grid-template-columns: auto minmax(0, 1fr)", css);
        Assert.Contains("min-width: 240px;", css);
        Assert.Contains("max-width: 33vw;", css);
        Assert.Contains("white-space: nowrap;", css);
        Assert.Contains("overflow: auto;", css);
    }
}
