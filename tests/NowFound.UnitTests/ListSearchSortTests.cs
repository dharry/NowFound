using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using NowFound.Application;
using NowFound.Application.Logging;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;
using NowFound.Web.Models;

namespace NowFound.UnitTests;

public class ListQueryModelTests
{
    private static IUrlHelper Url()
    {
        var url = new Mock<IUrlHelper>();
        url.Setup(u => u.Action(It.IsAny<UrlActionContext>())).Returns<UrlActionContext>(c =>
            "/List?" + string.Join('&', ((IDictionary<string, string?>)c.Values!).Where(p => p.Value is not null).Select(p => $"{p.Key}={p.Value}")));
        return url.Object;
    }

    [Fact]
    public void ToRouteValues_KeepsTheConditions_AndOmitsDefaults()
    {
        var model = new UsersQueryModel { Q = "bob", Role = "admin", Sort = "role", Desc = true };

        var values = model.ToRouteValues(3);
        var plain = new ListQueryModel().ToRouteValues(1);

        Assert.Equal(("bob", "role", "true", "3", "admin"), (values["Q"], values["Sort"], values["Desc"], values["Page"], values["Role"]));
        Assert.All(plain.Values, v => Assert.Null(v));
    }

    [Fact]
    public void ToRouteValues_DescIsOnlyKept_WhenASortIsGiven()
    {
        Assert.Null(new ListQueryModel { Desc = true }.ToRouteValues(1)["Desc"]);
        Assert.Equal("true", new ListQueryModel { Sort = "name", Desc = true }.ToRouteValues(1)["Desc"]);
    }

    [Fact]
    public void SortLink_TogglesTheCurrentColumn_AndStartsOtherColumnsAscending()
    {
        var model = new ListQueryModel { Sort = "name", Desc = false };

        var current = model.SortLink(Url(), "Name", "name", "name");
        var other = model.SortLink(Url(), "Path", "path", "name");

        Assert.True(current.Active);
        Assert.False(current.Descending);
        Assert.Equal("ascending", current.AriaSort);
        Assert.Contains("Desc=true", current.Url);
        Assert.False(other.Active);
        Assert.Equal("none", other.AriaSort);
        Assert.Contains("Sort=path", other.Url);
        Assert.DoesNotContain("Desc", other.Url);
    }

    [Fact]
    public void SortLink_WithoutASort_UsesTheDefault_AndResetsToPageOne()
    {
        var model = new ListQueryModel { Page = 4 };

        var link = model.SortLink(Url(), "Name", "name", "name");

        Assert.True(link.Active);
        Assert.Contains("Desc=true", link.Url); // 既定が昇順なので、押すと降順
        Assert.DoesNotContain("Page", link.Url);
    }

    [Fact]
    public void LogSearchViewModel_SortLink_DefaultsToNewestFirst()
    {
        var model = new LogSearchViewModel { User = "a" };

        var time = model.SortLink(Url(), "Time", LogSortKeys.Time);
        var user = model.SortLink(Url(), "User", LogSortKeys.User);

        Assert.True(time.Active);
        Assert.True(time.Descending);
        Assert.Contains("Sort=time", time.Url);
        Assert.DoesNotContain("Desc", time.Url); // 降順から押すと昇順
        Assert.Contains("User=a", user.Url);
    }
}

public sealed class ListSearchSortTests : IAsyncLifetime
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
    public async Task Users_CanBeSearchedByEmail_FilteredByRole_AndSorted()
    {
        var users = Get<IUserAdminService>();
        await users.CreateAsync(new CreateUserRequest("bob@example.com", "Passw0rd!", false, true));
        await users.CreateAsync(new CreateUserRequest("amy@example.com", "Passw0rd!", true, false));
        await users.CreateAsync(new CreateUserRequest("a_b@example.com", "Passw0rd!", false, false));
        await users.CreateAsync(new CreateUserRequest("axb@example.com", "Passw0rd!", false, false));

        var search = await users.ListPageAsync(1, 10, new UserListQuery("BOB"), Ct);
        var literal = await users.ListPageAsync(1, 10, new UserListQuery("a_b"), Ct);
        var admins = await users.ListPageAsync(1, 10, new UserListQuery(Role: "admin"), Ct);
        var normal = await users.ListPageAsync(1, 10, new UserListQuery(Role: "user"), Ct);
        var byEmailDesc = await users.ListPageAsync(1, 10, new UserListQuery(Sort: UserSortKeys.Email, Descending: true), Ct);
        var byRoleDesc = await users.ListPageAsync(1, 10, new UserListQuery(Sort: UserSortKeys.Role, Descending: true), Ct);
        var byMustChange = await users.ListPageAsync(1, 10, new UserListQuery(Sort: UserSortKeys.MustChange, Descending: true), Ct);
        var unknown = await users.ListPageAsync(1, 10, new UserListQuery(Sort: "bogus", Descending: true), Ct);

        Assert.Equal(["bob@example.com"], search.Items.Select(u => u.Email));
        Assert.Equal(["a_b@example.com"], literal.Items.Select(u => u.Email)); // _ は任意の 1 文字ではなく、文字として扱う
        Assert.Equal(["admin@nowfound.invalid", "amy@example.com"], admins.Items.Select(u => u.Email));
        Assert.Equal(3, normal.TotalCount);
        Assert.Equal(["bob@example.com", "axb@example.com", "amy@example.com", "admin@nowfound.invalid", "a_b@example.com"], byEmailDesc.Items.Select(u => u.Email));
        Assert.All(byRoleDesc.Items.Take(2), u => Assert.True(u.IsAdmin));
        Assert.Contains(byMustChange.Items.Take(2), u => u.Email == "bob@example.com");
        Assert.Equal("a_b@example.com", unknown.Items[0].Email); // 不明なキーはメールアドレス昇順
    }

    [Fact]
    public async Task Groups_AndFolders_CanBeSearchedAndSortedByEveryColumn()
    {
        var settings = Get<IAdminSettingsService>();
        var temp = Path.GetTempPath();
        await settings.SaveFolderAsync(new FolderEdit { Name = "Alpha", Path = Path.Combine(temp, "zz-alpha"), ExcludePatterns = ["a", "b"] }, Ct);
        await settings.SaveFolderAsync(new FolderEdit { Name = "Beta", Path = Path.Combine(temp, "aa-beta"), IsShared = true, ShareName = "BetaShare" }, Ct);
        var folderIds = (await settings.ListFoldersAsync(Ct)).ToDictionary(f => f.Name, f => f.Id);
        await settings.SaveGroupAsync(new GroupEdit { Name = "Zeta", Description = "alpha team", FolderIds = [folderIds["Alpha"], folderIds["Beta"]] }, Ct);
        await settings.SaveGroupAsync(new GroupEdit { Name = "Eta", Description = "beta team" }, Ct);

        var byPath = await settings.ListFoldersPageAsync(1, 10, new FolderListQuery(Sort: FolderSortKeys.Path), Ct);
        var byExcludesDesc = await settings.ListFoldersPageAsync(1, 10, new FolderListQuery(Sort: FolderSortKeys.Excludes, Descending: true), Ct);
        var byGroupsDesc = await settings.ListFoldersPageAsync(1, 10, new FolderListQuery(Sort: FolderSortKeys.Groups, Descending: true), Ct);
        var folderByPathSearch = await settings.ListFoldersPageAsync(1, 10, new FolderListQuery("aa-b"), Ct);
        var folderByName = await settings.ListFoldersPageAsync(1, 10, new FolderListQuery("ALPHA"), Ct);
        var byShare = await settings.ListFoldersPageAsync(1, 10, new FolderListQuery(Sort: FolderSortKeys.Share), Ct);
        var byShareDesc = await settings.ListFoldersPageAsync(1, 10, new FolderListQuery(Sort: FolderSortKeys.Share, Descending: true), Ct);
        var folderByShareName = await settings.ListFoldersPageAsync(1, 10, new FolderListQuery("betashare"), Ct);

        var byMembers = await settings.ListGroupsPageAsync(1, 10, new GroupListQuery(Sort: GroupSortKeys.Members, Descending: true), Ct);
        var byFolders = await settings.ListGroupsPageAsync(1, 10, new GroupListQuery(Sort: GroupSortKeys.Folders, Descending: true), Ct);
        var byNameDesc = await settings.ListGroupsPageAsync(1, 10, new GroupListQuery(Sort: GroupSortKeys.Name, Descending: true), Ct);
        var byDescription = await settings.ListGroupsPageAsync(1, 10, new GroupListQuery(Sort: GroupSortKeys.Description), Ct);
        var groupSearch = await settings.ListGroupsPageAsync(1, 10, new GroupListQuery("alpha"), Ct);

        Assert.Equal(["Beta", "Alpha"], byPath.Items.Select(f => f.Name));
        Assert.Equal("Alpha", byExcludesDesc.Items[0].Name);
        Assert.Equal(["Alpha", "Beta"], byGroupsDesc.Items.Select(f => f.Name)); // 同数は名前順
        Assert.Equal(["Beta"], folderByPathSearch.Items.Select(f => f.Name));
        Assert.Equal(["Alpha"], folderByName.Items.Select(f => f.Name));
        Assert.Equal(["Beta", "Alpha"], byShare.Items.Select(f => f.Name)); // 共有フォルダが先
        Assert.Equal(["Alpha", "Beta"], byShareDesc.Items.Select(f => f.Name));
        Assert.Equal(["Beta"], folderByShareName.Items.Select(f => f.Name));
        Assert.True(byShare.Items[0].IsShared);
        Assert.Equal("BetaShare", byShare.Items[0].ShareName);
        Assert.False(byShare.Items[1].IsShared);

        Assert.Equal("デフォルトグループ", byMembers.Items[0].Name); // 初期管理者が所属
        Assert.Equal("Zeta", byFolders.Items[0].Name);
        Assert.Equal(["デフォルトグループ", "Zeta", "Eta"], byNameDesc.Items.Select(g => g.Name));
        Assert.Equal("Zeta", byDescription.Items[0].Name); // "alpha team" が先頭
        Assert.Equal(["Zeta"], groupSearch.Items.Select(g => g.Name));
    }

    [Fact]
    public async Task SavedSearches_CanBeSearched_FilteredByVisibility_AndSorted()
    {
        var adminId = (await Get<IUserAdminService>().ListAsync(Ct)).Single().Id;
        var service = Get<ISavedSearchService>();
        var query = new SavedQuery(SearchOperator.And, [new SearchCondition { Field = SearchField.FileName, Text = "x" }], null, null, SearchSortKey.Path, false);
        await service.CreateAsync(adminId, true, new SavedSearchMeta("delta", "first note", false, true), query, Ct);
        await service.CreateAsync(adminId, true, new SavedSearchMeta("alpha", "zzz", true, false), query, Ct);
        await service.CreateAsync(adminId, true, new SavedSearchMeta("charlie", null, false, false), query, Ct);

        var all = await service.ListPageAsync(adminId, true, 1, 10, null, Ct);
        var byNameDesc = await service.ListPageAsync(adminId, true, 1, 10, new SavedSearchListQuery(Sort: BookmarkSortKeys.Name, Descending: true), Ct);
        var byDescription = await service.ListPageAsync(adminId, true, 1, 10, new SavedSearchListQuery(Sort: BookmarkSortKeys.Description), Ct);
        var byUpdatedDesc = await service.ListPageAsync(adminId, true, 1, 10, new SavedSearchListQuery(Sort: BookmarkSortKeys.Updated, Descending: true), Ct);
        var mine = await service.ListPageAsync(adminId, true, 1, 10, new SavedSearchListQuery(Visibility: "mine"), Ct);
        var shared = await service.ListPageAsync(adminId, true, 1, 10, new SavedSearchListQuery(Visibility: "public"), Ct);
        var byDescriptionSearch = await service.ListPageAsync(adminId, true, 1, 10, new SavedSearchListQuery("NOTE"), Ct);
        var byOwnerSearch = await service.ListPageAsync(adminId, true, 1, 10, new SavedSearchListQuery("nowfound.invalid"), Ct);

        Assert.Equal(["delta", "alpha", "charlie"], all.Items.Select(s => s.Name)); // 固定が先頭、次に名前順
        Assert.Equal(["delta", "charlie", "alpha"], byNameDesc.Items.Select(s => s.Name)); // 指定すると固定は無視
        Assert.Equal("charlie", byDescription.Items[0].Name); // 説明なしが先頭
        Assert.Equal("charlie", byUpdatedDesc.Items[0].Name);
        Assert.Equal(["charlie", "delta"], mine.Items.Select(s => s.Name).Order());
        Assert.Equal(["alpha"], shared.Items.Select(s => s.Name));
        Assert.Equal(["delta"], byDescriptionSearch.Items.Select(s => s.Name));
        Assert.Equal(3, byOwnerSearch.TotalCount);
    }

    [Fact]
    public async Task OperationLog_CanBeSortedByEveryColumn_AndDefaultsToNewestFirst()
    {
        var service = Get<IOperationLogService>();
        var db = Get<OperationLogDbContext>();
        var now = DateTime.UtcNow;
        db.OperationLogs.AddRange(
            new OperationLog { Timestamp = now.AddMinutes(-3), Operation = OperationType.Login, UserEmail = "c@example.com", IpAddress = "10.0.0.2" },
            new OperationLog { Timestamp = now.AddMinutes(-2), Operation = OperationType.AdminChange, UserEmail = "a@example.com", IpAddress = "10.0.0.3" },
            new OperationLog { Timestamp = now.AddMinutes(-1), Operation = OperationType.Search, UserEmail = "b@example.com", IpAddress = "10.0.0.1" });
        await db.SaveChangesAsync(Ct);

        async Task<string[]> Users(string? sort, bool desc) =>
            [.. (await service.SearchAsync(new OperationLogQuery(null, null, null, null, 1, 10, sort, desc), Ct)).Items.Select(i => i.UserEmail)];

        Assert.Equal(["b@example.com", "a@example.com", "c@example.com"], await Users(null, false));
        Assert.Equal(["b@example.com", "a@example.com", "c@example.com"], await Users(LogSortKeys.Time, true));
        Assert.Equal(["c@example.com", "a@example.com", "b@example.com"], await Users(LogSortKeys.Time, false));
        Assert.Equal(["a@example.com", "b@example.com", "c@example.com"], await Users(LogSortKeys.User, false));
        Assert.Equal(["c@example.com", "b@example.com", "a@example.com"], await Users(LogSortKeys.User, true));
        Assert.Equal(["a@example.com", "c@example.com", "b@example.com"], await Users(LogSortKeys.Operation, false));
        Assert.Equal(["b@example.com", "c@example.com", "a@example.com"], await Users(LogSortKeys.Ip, false));
        Assert.Equal(["a@example.com", "c@example.com", "b@example.com"], await Users(LogSortKeys.Ip, true));
        Assert.Equal(["b@example.com", "a@example.com", "c@example.com"], await Users("bogus", false));
    }
}
