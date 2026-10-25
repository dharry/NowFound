using Microsoft.Extensions.DependencyInjection;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.IntegrationTests;

/// <summary>ページ移動リンクは、検索結果・操作ログとも一覧の上と下の両方に出る。</summary>
public sealed class PagerFlowTests : IDisposable
{
    private const string PagerMarker = "aria-label=\"ページ移動\"";

    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static int Count(string html, string marker) => html.Split(marker).Length - 1;

    [Fact]
    public async Task SearchResults_ShowThePager_AboveAndBelowTheTable()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "Temp"), new("Path", root)]);
        await admin.PostFormAsync("/Settings", "/Settings", [new("PageSize", "1")]);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
            var directory = new DirectoryEntry { SearchFolderId = 1, Path = root };
            files.Directories.Add(directory);
            await files.SaveChangesAsync(TestContext.Current.CancellationToken);
            var now = DateTime.UtcNow;
            files.Files.AddRange(new[] { "a.dat", "b.dat", "c.dat" }.Select(name => new FileEntry
            {
                SearchFolderId = 1, DirectoryId = directory.Id, Name = name, Extension = "dat", Size = 1, ModifiedAt = now, CreatedAt = now,
            }));
            await files.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var html = await admin.GetStringAsync("/?FolderId=1", TestContext.Current.CancellationToken);

        Assert.Equal(2, Count(html, PagerMarker));
        Assert.Equal(2, Count(html, "次へ</a>"));
        Assert.True(html.IndexOf(PagerMarker, StringComparison.Ordinal) < html.IndexOf("<table", StringComparison.Ordinal));
        Assert.True(html.LastIndexOf(PagerMarker, StringComparison.Ordinal) > html.LastIndexOf("</table>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OperationLog_ShowsThePager_AboveAndBelowTheTable()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        await admin.PostFormAsync("/Settings", "/Settings", [new("PageSize", "500"), new("LogPageSize", "1")]);

        var html = await admin.GetStringAsync("/Logs", TestContext.Current.CancellationToken);

        Assert.Equal(2, Count(html, PagerMarker));
        Assert.True(html.IndexOf(PagerMarker, StringComparison.Ordinal) < html.IndexOf("<table", StringComparison.Ordinal));
        Assert.True(html.LastIndexOf(PagerMarker, StringComparison.Ordinal) > html.LastIndexOf("</table>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pager_IsHidden_WhenEverythingFitsOnOnePage()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var html = await admin.GetStringAsync("/Logs", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(PagerMarker, html);
    }

    [Fact]
    public async Task SearchResults_ShowsCurrentRange_AndPageSizeInfo()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "Temp2"), new("Path", root)]);
        await admin.PostFormAsync("/Settings", "/Settings", [new("PageSize", "2")]);

        int folderId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            folderId = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                System.Linq.Queryable.OrderByDescending(appDb.SearchFolders, f => f.Id),
                TestContext.Current.CancellationToken)).Id;

            var files = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
            var directory = new DirectoryEntry { SearchFolderId = folderId, Path = root };
            files.Directories.Add(directory);
            await files.SaveChangesAsync(TestContext.Current.CancellationToken);
            var now = DateTime.UtcNow;
            files.Files.AddRange(new[] { "x1.dat", "x2.dat", "x3.dat" }.Select(name => new FileEntry
            {
                SearchFolderId = folderId, DirectoryId = directory.Id, Name = name, Extension = "dat", Size = 1, ModifiedAt = now, CreatedAt = now,
            }));
            await files.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var html = await admin.GetStringAsync($"/?FolderId={folderId}", TestContext.Current.CancellationToken);

        Assert.Contains("3 件中 1～2 件", html);
        Assert.Contains("表示件数: 2 件", html);
        Assert.Contains("/Account/Preferences#PageSize", html);
        Assert.Contains("(1–2 / 3 件)", html);
    }

    [Fact]
    public async Task UserList_ShowsCurrentRange_AndAdminListPageSize()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var html = await admin.GetStringAsync("/Users", TestContext.Current.CancellationToken);
        Assert.Contains("1 件中 1～1 件", html);
        Assert.Contains("表示件数: 50 件", html);
        Assert.Contains("/Settings#AdminListPageSize", html);
    }
}
