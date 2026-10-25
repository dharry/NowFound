using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

public sealed class SearchServiceExportTests : IAsyncLifetime
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mk-export"));
    private static readonly string OtherRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mk-export-other"));

    private TestHost _host = null!;
    private IServiceScope _scope = null!;
    private ISearchService _search = null!;
    private string _adminId = null!;
    private string _memberId = null!;
    private int _folderId;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _scope = _host.Services.CreateScope();
        var sp = _scope.ServiceProvider;
        _search = sp.GetRequiredService<ISearchService>();
        var users = sp.GetRequiredService<IUserAdminService>();
        var settings = sp.GetRequiredService<IAdminSettingsService>();

        await users.CreateAsync(new CreateUserRequest("member@example.com", "Passw0rd!", false, false));
        var all = await users.ListAsync();
        _adminId = all.Single(u => u.IsAdmin).Id;
        _memberId = all.Single(u => u.Email == "member@example.com").Id;

        await settings.SaveFolderAsync(new FolderEdit { Name = "Main", Path = Root });
        await settings.SaveFolderAsync(new FolderEdit { Name = "Other", Path = OtherRoot });
        var folders = await settings.ListFoldersAsync();
        _folderId = folders.Single(f => f.Name == "Main").Id;
        var otherId = folders.Single(f => f.Name == "Other").Id;
        await settings.SaveGroupAsync(new GroupEdit { Name = "G", UserIds = [_memberId], FolderIds = [_folderId] });

        var seeder = new FilesSeeder(sp.GetRequiredService<FilesDbContext>());
        for (var i = 1; i <= 25; i++)
        {
            await seeder.AddAsync(_folderId, Root, $"file{i:00}.txt", "txt", i, DateTime.UtcNow, "alice");
        }

        await seeder.AddAsync(_folderId, Path.Combine(Root, "sub"), "inner.log", "log", 1);
        await seeder.AddAsync(otherId, OtherRoot, "secret.txt", "txt", 9);
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    private static SearchRequest Request(string userId, bool isAdmin, int? folderId = null, string? path = null, SearchSortKey sort = SearchSortKey.Path, bool desc = false, params SearchCondition[] conditions) =>
        new(userId, isAdmin, conditions, SearchOperator.And, folderId, path, 1, sort, desc);

    private async Task<List<SearchResultItem>> ExportAsync(SearchRequest request, int maxRows = 1000)
    {
        var items = new List<SearchResultItem>();
        await foreach (var item in _search.ExportAsync(request, maxRows, TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        return items;
    }

    [Fact]
    public async Task Export_ReturnsEveryRow_NotJustOnePage()
    {
        var ct = TestContext.Current.CancellationToken;
        await _scope.ServiceProvider.GetRequiredService<IAdminSettingsService>().SetDefaultPageSizeAsync(10, ct);
        var request = Request(_adminId, true);

        var page = await _search.SearchAsync(request, ct);
        var exported = await ExportAsync(request);

        Assert.Equal(10, page.Items.Count);
        Assert.Equal(page.TotalCount, exported.Count);
        Assert.Equal(27, exported.Count);
    }

    [Fact]
    public async Task Export_MatchesSearchOrderAndContent()
    {
        var request = Request(_adminId, true, sort: SearchSortKey.Size, desc: true);

        var page = await _search.SearchAsync(request, TestContext.Current.CancellationToken);
        var exported = await ExportAsync(request);

        Assert.Equal(page.Items.Select(i => i.FullPath), exported.Take(page.Items.Count).Select(i => i.FullPath));
        Assert.Equal(25, exported[0].Size);
    }

    [Fact]
    public async Task Export_AppliesPermissions_LikeSearch()
    {
        var member = await ExportAsync(Request(_memberId, false));

        Assert.DoesNotContain(member, i => i.Name == "secret.txt");
        Assert.Equal(26, member.Count);
        Assert.Contains(await ExportAsync(Request(_adminId, true)), i => i.Name == "secret.txt");
    }

    [Fact]
    public async Task Export_AppliesConditions_AndFolderScope()
    {
        var onlyLog = await ExportAsync(Request(_adminId, true, conditions: new SearchCondition { Field = SearchField.Extension, Text = "log" }));
        var scoped = await ExportAsync(Request(_adminId, true, _folderId, Path.Combine(Root, "sub")));

        Assert.Equal(["inner.log"], onlyLog.Select(i => i.Name));
        Assert.Equal(["inner.log"], scoped.Select(i => i.Name));
    }

    [Fact]
    public async Task Export_StopsAtMaxRows()
    {
        Assert.Equal(5, (await ExportAsync(Request(_adminId, true), maxRows: 5)).Count);
        Assert.Empty(await ExportAsync(Request(_adminId, true), maxRows: 0));
    }

    [Fact]
    public async Task Export_ForAnUnavailableFolder_IsEmpty()
    {
        Assert.Empty(await ExportAsync(Request(_memberId, false, folderId: 9999)));
        Assert.Empty(await ExportAsync(Request(_adminId, true, _folderId, Path.Combine(OtherRoot, "x"))));
    }

    [Fact]
    public async Task Export_WhenCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _search.ExportAsync(Request(_adminId, true), 100, cts.Token))
            {
            }
        });
    }
}
