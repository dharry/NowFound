using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Indexing;

namespace NowFound.UnitTests;

/// <summary>実ファイルを Indexer で収集し、検索サービスで引く。収集と検索でパスの扱いが食い違わないことを確認する。</summary>
public sealed class IndexAndSearchEndToEndTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nowfound-e2e-{Guid.NewGuid():N}");
    private TestHost _host = null!;
    private IServiceScope _scope = null!;
    private ISearchService _search = null!;
    private string _adminId = null!;
    private int _folderId;

    public async ValueTask InitializeAsync()
    {
        foreach (var relative in new[] { "a/x.txt", "a b/x.txt", "a/sub/z.log", "あ/い.txt", "B/y.txt", "top.txt" })
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "x");
        }

        _host = await TestHost.CreateAsync();
        _scope = _host.Services.CreateScope();
        var sp = _scope.ServiceProvider;
        _search = sp.GetRequiredService<ISearchService>();
        _adminId = (await sp.GetRequiredService<IUserAdminService>().ListAsync()).Single(u => u.IsAdmin).Id;

        var settings = sp.GetRequiredService<IAdminSettingsService>();
        await settings.SaveFolderAsync(new FolderEdit { Name = "E2E", Path = _root });
        _folderId = (await settings.ListFoldersAsync()).Single().Id;

        var targets = await IndexSynchronizer.LoadTargetsAsync(sp.GetRequiredService<AppDbContext>());
        await new IndexSynchronizer(new FileScanner(), batchSize: 3, racyWindow: TimeSpan.Zero)
            .SyncAllAsync(sp.GetRequiredService<FilesDbContext>(), targets);
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private static SearchCondition C(SearchField field, string text) => new() { Field = field, Text = text };

    private Task<SearchPage> SearchAsync(
        SearchCondition[]? conditions = null, string? folderPath = null, SearchSortKey sort = SearchSortKey.Path, bool desc = false) =>
        _search.SearchAsync(new SearchRequest(
            _adminId, true, conditions ?? [], SearchOperator.And, _folderId, folderPath, 1, sort, desc));

    [Fact]
    public async Task DefaultOrder_IsFolderPathThenNameOrder()
    {
        var page = await SearchAsync();

        var items = page.Items.Select(i => (i.FolderPath, i.Name)).ToList();
        // フォルダパス昇順、同フォルダ内は名前昇順（カラム別ソートでインデックスを適用）
        Assert.Equal(
            [.. items.OrderBy(i => i.FolderPath, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal)],
            items);
        var fullPaths = page.Items.Select(i => i.FullPath).ToList();
        Assert.True(fullPaths.IndexOf(Path.Combine(_root, "a", "x.txt")) < fullPaths.IndexOf(Path.Combine(_root, "a b", "x.txt")));
    }

    [Fact]
    public async Task DescendingOrder_IsTheExactReverse()
    {
        var ascending = (await SearchAsync()).Items.Select(i => i.FullPath).ToList();
        var descending = (await SearchAsync(desc: true)).Items.Select(i => i.FullPath).ToList();

        Assert.Equal([.. ascending.AsEnumerable().Reverse()], descending);
    }

    [Fact]
    public async Task EveryResult_PointsToAnExistingFileOrFolder()
    {
        var page = await SearchAsync();

        Assert.NotEmpty(page.Items);
        foreach (var item in page.Items)
        {
            Assert.True(
                item.IsDirectory ? Directory.Exists(item.FullPath) : File.Exists(item.FullPath),
                $"存在しないパス: {item.FullPath}");
            Assert.Equal(item.FolderPath, Path.GetDirectoryName(item.FullPath));
        }
    }

    [Fact]
    public async Task FileNameSearch_FindsSameNameInEveryFolder()
    {
        var page = await SearchAsync([C(SearchField.FileName, "x.txt")]);

        Assert.Equal(
            [Path.Combine(_root, "a", "x.txt"), Path.Combine(_root, "a b", "x.txt")],
            page.Items.Select(i => i.FullPath));
    }

    [Theory]
    [InlineData("a b", 1)]     // 3 文字: フォルダ側の FTS
    [InlineData("sub", 1)]
    [InlineData("あ", 1)]      // 1 文字: LIKE
    [InlineData("\\B", 1)]     // 2 文字: LIKE（バックスラッシュも文字として扱う）
    public async Task FolderNameSearch_MatchesFilesInsideMatchingFolders(string term, int expectedFiles)
    {
        var page = await SearchAsync([C(SearchField.FolderName, term)]);

        Assert.Equal(expectedFiles, page.Items.Count(i => !i.IsDirectory));
        Assert.All(page.Items, i => Assert.Contains(term, i.FolderPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FolderScope_IncludesTheFolderItself_AndEverythingBelow_ButNotLookalikeFolders()
    {
        var page = await SearchAsync(folderPath: Path.Combine(_root, "a"));

        var names = page.Items.Select(i => i.FullPath.Substring(_root.Length + 1)).Order().ToList();
        Assert.Equal(
            [
                "a", Path.Combine("a", "sub"), Path.Combine("a", "sub", "z.log"), Path.Combine("a", "x.txt"),
            ],
            names);
    }

    [Fact]
    public async Task AllField_MatchesNameFolderAndExtension()
    {
        var byFolder = await SearchAsync([C(SearchField.All, "a b")]);
        var byExtension = await SearchAsync([C(SearchField.All, "log")]);

        Assert.Contains(byFolder.Items, i => i.Name == "x.txt" && i.FolderPath.EndsWith("a b"));
        Assert.Equal(["z.log"], byExtension.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task SubFolders_ListChildrenWithCombinedPaths()
    {
        var top = await _search.GetSubFoldersAsync(_adminId, true, _folderId, null, TestContext.Current.CancellationToken);
        var below = await _search.GetSubFoldersAsync(_adminId, true, _folderId, Path.Combine(_root, "a"), TestContext.Current.CancellationToken);

        Assert.Equal(["B", "a", "a b", "あ"], top.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Equal([Path.Combine(_root, "a", "sub")], below.Select(s => s.Path));
    }
}
