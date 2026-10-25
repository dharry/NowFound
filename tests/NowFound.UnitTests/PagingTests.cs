using Microsoft.Extensions.DependencyInjection;
using NowFound.Application;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Web.Models;

namespace NowFound.UnitTests;

public class PagedResultTests
{
    [Theory]
    [InlineData(1, 0, 10, 1)]
    [InlineData(0, 25, 10, 1)]
    [InlineData(-3, 25, 10, 1)]
    [InlineData(3, 25, 10, 3)]
    [InlineData(4, 25, 10, 3)]
    [InlineData(99, 20, 10, 2)]
    public void ClampPage_KeepsThePageInsideTheAvailableRange(int page, int total, int pageSize, int expected) =>
        Assert.Equal(expected, PagedResult<int>.ClampPage(page, total, pageSize));

    [Fact]
    public void TotalPages_RoundsUp_AndIsZeroWithoutAPageSize()
    {
        Assert.Equal(3, new PagedResult<int>([], 1, 10, 21).TotalPages);
        Assert.Equal(0, new PagedResult<int>([], 1, 0, 21).TotalPages);
    }

    [Fact]
    public void PagerModel_From_LinksOnlyToExistingNeighbours()
    {
        var middle = PagerModel.From(new PagedResult<int>([], 2, 10, 30), p => $"/x?page={p}");
        var first = PagerModel.From(new PagedResult<int>([], 1, 10, 30), p => $"/x?page={p}");
        var single = PagerModel.From(new PagedResult<int>([], 1, 10, 5), p => $"/x?page={p}");

        Assert.Equal(("/x?page=1", "/x?page=3"), (middle.PreviousUrl, middle.NextUrl));
        Assert.Equal((null, "/x?page=2"), (first.PreviousUrl, first.NextUrl));
        Assert.False(single.IsVisible);
    }

    [Theory]
    [InlineData(1, 10, 0, 0, 0)]
    [InlineData(1, 10, 25, 1, 10)]
    [InlineData(2, 10, 25, 11, 20)]
    [InlineData(3, 10, 25, 21, 25)]
    [InlineData(1, 50, 15, 1, 15)]
    public void PagerModel_CalculatesFromAndToCorrectly(int page, int pageSize, int totalCount, int expectedFrom, int expectedTo)
    {
        var model = new PagerModel(page, 3, null, null, pageSize, totalCount);
        Assert.Equal(expectedFrom, model.StartItem);
        Assert.Equal(expectedTo, model.EndItem);
    }

    [Fact]
    public void PagerModel_From_SearchPage_PopulatesAllProperties()
    {
        var searchPage = new SearchPage([], 2, 50, 120);
        var pager = PagerModel.From(searchPage, p => $"/search?p={p}");

        Assert.Equal(2, pager.Page);
        Assert.Equal(3, pager.TotalPages);
        Assert.Equal(50, pager.PageSize);
        Assert.Equal(120, pager.TotalCount);
        Assert.Equal(51, pager.StartItem);
        Assert.Equal(100, pager.EndItem);
        Assert.Equal("/search?p=1", pager.PreviousUrl);
        Assert.Equal("/search?p=3", pager.NextUrl);
        Assert.True(pager.IsVisible);
    }
}

public sealed class ListPagingServiceTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private IServiceScope _scope = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _scope = _host.Services.CreateScope();
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    private T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Users_ArePagedByEmail_AndOutOfRangePagesAreClamped()
    {
        var users = Get<IUserAdminService>();
        foreach (var name in new[] { "c", "a", "e", "b", "d" })
        {
            await users.CreateAsync(new CreateUserRequest($"{name}@example.com", "Passw0rd!", false, false));
        }

        var first = await users.ListPageAsync(1, 2, null, Ct);
        var last = await users.ListPageAsync(99, 2, null, Ct);

        Assert.Equal(6, first.TotalCount); // 初期管理者を含む
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(["a@example.com", "admin@nowfound.invalid"], first.Items.Select(u => u.Email));
        Assert.True(first.Items[1].IsAdmin);
        Assert.Equal(3, last.Page);
        Assert.Equal(["d@example.com", "e@example.com"], last.Items.Select(u => u.Email));
    }

    [Fact]
    public async Task Folders_AndGroups_ArePagedByName()
    {
        var settings = Get<IAdminSettingsService>();
        foreach (var name in new[] { "C", "A", "B" })
        {
            await settings.SaveFolderAsync(new FolderEdit { Name = name, Path = Path.Combine(Path.GetTempPath(), $"mk-page-{name}") }, Ct);
            await settings.SaveGroupAsync(new GroupEdit { Name = $"G{name}" }, Ct);
        }

        var folders = await settings.ListFoldersPageAsync(2, 2, null, Ct);
        var groups = await settings.ListGroupsPageAsync(1, 3, null, Ct);

        Assert.Equal(3, folders.TotalCount);
        Assert.Equal(["C"], folders.Items.Select(f => f.Name));
        Assert.Equal(4, groups.TotalCount); // 既定のグループを含む
        Assert.Equal(2, groups.TotalPages);
        Assert.Equal(3, groups.Items.Count);
    }

    [Fact]
    public async Task SavedSearches_KeepTheListOrder_AcrossPages()
    {
        var users = Get<IUserAdminService>();
        var adminId = (await users.ListAsync(Ct)).Single().Id;
        var service = Get<ISavedSearchService>();
        var query = new SavedQuery(SearchOperator.And, [new SearchCondition { Field = SearchField.FileName, Text = "x" }], null, null, SearchSortKey.Path, false);
        foreach (var name in new[] { "d", "b", "a", "c" })
        {
            await service.CreateAsync(adminId, true, new SavedSearchMeta(name, null, false, name == "d"), query, Ct);
        }

        var first = await service.ListPageAsync(adminId, true, 1, 3, null, Ct);
        var second = await service.ListPageAsync(adminId, true, 2, 3, null, Ct);

        Assert.Equal(["d", "a", "b"], first.Items.Select(s => s.Name)); // 固定が先頭、次に名前順
        Assert.Equal(["c"], second.Items.Select(s => s.Name));
        Assert.Equal(4, second.TotalCount);
    }

    [Fact]
    public async Task AdminListPageSize_DefaultsTo50_ValidatesRange_AndIsSeparateFromTheOthers()
    {
        var settings = Get<IAdminSettingsService>();

        Assert.Equal(SystemSetting.DefaultListPageSize, await settings.GetAdminListPageSizeAsync(Ct));
        Assert.True((await settings.SetAdminListPageSizeAsync(20, Ct)).Succeeded);
        Assert.False((await settings.SetAdminListPageSizeAsync(0, Ct)).Succeeded);
        Assert.False((await settings.SetAdminListPageSizeAsync(PageSizeLimits.Max + 1, Ct)).Succeeded);

        Assert.Equal(20, await settings.GetAdminListPageSizeAsync(Ct));
        Assert.Equal(SystemSetting.DefaultPageSize, await settings.GetDefaultPageSizeAsync(Ct));
        Assert.Equal(SystemSetting.DefaultLogPageSize, await settings.GetLogPageSizeAsync(Ct));
    }

    [Fact]
    public async Task BookmarkPageSize_IsPerUser_NullByDefault_AndCanBeCleared()
    {
        var users = Get<IUserAdminService>();
        var adminId = (await users.ListAsync(Ct)).Single().Id;

        Assert.Null(await users.GetBookmarkPageSizeAsync(adminId, Ct));
        Assert.True((await users.SetBookmarkPageSizeAsync(adminId, 15, Ct)).Succeeded);
        Assert.Equal(15, await users.GetBookmarkPageSizeAsync(adminId, Ct));
        Assert.False((await users.SetBookmarkPageSizeAsync(adminId, 0, Ct)).Succeeded);
        Assert.False((await users.SetBookmarkPageSizeAsync("missing", 10, Ct)).Succeeded);
        Assert.True((await users.SetBookmarkPageSizeAsync(adminId, null, Ct)).Succeeded);
        Assert.Null(await users.GetBookmarkPageSizeAsync(adminId, Ct));
    }
}
