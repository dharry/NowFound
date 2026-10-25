using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Search;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace NowFound.UnitTests;

public sealed class SavedSearchServiceTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private IServiceScope _scope = null!;
    private ISavedSearchService _service = null!;
    private string _admin = null!;
    private string _admin2 = null!;
    private string _user1 = null!;
    private string _user2 = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _scope = _host.Services.CreateScope();
        _service = _scope.ServiceProvider.GetRequiredService<ISavedSearchService>();
        var users = _scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        await users.CreateAsync(new CreateUserRequest("admin2@example.com", "Passw0rd!", true, false));
        await users.CreateAsync(new CreateUserRequest("user1@example.com", "Passw0rd!", false, false));
        await users.CreateAsync(new CreateUserRequest("user2@example.com", "Passw0rd!", false, false));
        var all = await users.ListAsync();
        _admin = all.Single(u => u.Email == "admin@nowfound.invalid").Id;
        _admin2 = all.Single(u => u.Email == "admin2@example.com").Id;
        _user1 = all.Single(u => u.Email == "user1@example.com").Id;
        _user2 = all.Single(u => u.Email == "user2@example.com").Id;
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    private static SavedQuery Query(string text = "report", SearchOperator op = SearchOperator.And, int? folderId = null) =>
        new(op, [new SearchCondition { Field = SearchField.FileName, Text = text }], folderId, null, SearchSortKey.Path, false);

    private static SavedSearchMeta Meta(string name, bool isPublic = false, bool pinned = false, string? description = null) =>
        new(name, description, isPublic, pinned);

    private async Task<int> CreateAsync(string userId, bool isAdmin, string name, bool isPublic = false, bool pinned = false)
    {
        var result = await _service.CreateAsync(userId, isAdmin, Meta(name, isPublic, pinned), Query());
        Assert.True(result.Result.Succeeded, string.Join(",", result.Result.Errors));
        return result.Id!.Value;
    }

    [Fact]
    public async Task Private_IsVisibleOnlyToOwner_EvenAdminCannotSeeIt()
    {
        var id = await CreateAsync(_user1, false, "mine");

        Assert.Single(await _service.ListAsync(_user1, false, TestContext.Current.CancellationToken));
        Assert.Empty(await _service.ListAsync(_user2, false, TestContext.Current.CancellationToken));
        Assert.Empty(await _service.ListAsync(_admin, true, TestContext.Current.CancellationToken));
        Assert.NotNull(await _service.GetAsync(id, _user1, false, TestContext.Current.CancellationToken));
        Assert.Null(await _service.GetAsync(id, _user2, false, TestContext.Current.CancellationToken));
        Assert.Null(await _service.GetAsync(id, _admin, true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Public_CanOnlyBeCreatedByAdmin_AndIsVisibleToEveryone()
    {
        var denied = await _service.CreateAsync(_user1, false, Meta("shared", isPublic: true), Query(), TestContext.Current.CancellationToken);
        Assert.False(denied.Result.Succeeded);
        Assert.Equal("Error.SavedSearch.PublicCreateAdminOnly", denied.Result.Errors.Single().Key);
        Assert.Empty(await _service.ListAsync(_user1, false, TestContext.Current.CancellationToken));

        var id = await CreateAsync(_admin, true, "shared", isPublic: true);

        foreach (var (userId, isAdmin) in new[] { (_admin, true), (_admin2, true), (_user1, false), (_user2, false) })
        {
            var item = Assert.Single(await _service.ListAsync(userId, isAdmin, TestContext.Current.CancellationToken));
            Assert.True(item.IsPublic);
            Assert.Equal(isAdmin, item.CanEdit);
            Assert.NotNull(await _service.GetAsync(id, userId, isAdmin, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Public_OwnerEmailIsExposed_AndCanEditIsPerViewer()
    {
        await CreateAsync(_admin, true, "shared", isPublic: true);
        await CreateAsync(_user1, false, "mine");

        var forUser = await _service.ListAsync(_user1, false, TestContext.Current.CancellationToken);

        Assert.Equal(["mine", "shared"], forUser.Select(s => s.Name));
        Assert.True(forUser.Single(s => s.Name == "mine").CanEdit);
        Assert.False(forUser.Single(s => s.Name == "shared").CanEdit);
        Assert.Equal("admin@nowfound.invalid", forUser.Single(s => s.Name == "shared").OwnerEmail);
    }

    [Fact]
    public async Task UsersCannotChangeOrDeletePublic_AdminsCan()
    {
        var id = await CreateAsync(_admin, true, "shared", isPublic: true);

        var userUpdate = await _service.UpdateAsync(id, _user1, false, Meta("hacked", isPublic: true), null, TestContext.Current.CancellationToken);
        var userDelete = await _service.DeleteAsync(id, _user1, false, TestContext.Current.CancellationToken);
        var userOverwrite = await _service.OverwriteQueryAsync(id, _user1, false, Query("x"), TestContext.Current.CancellationToken);

        Assert.False(userUpdate.Succeeded);
        Assert.False(userDelete.Succeeded);
        Assert.False(userOverwrite.Succeeded);
        Assert.Equal("shared", (await _service.GetAsync(id, _user1, false, TestContext.Current.CancellationToken))!.Summary.Name);

        // 作成者以外の管理者でも変更・削除できる。
        Assert.True((await _service.UpdateAsync(id, _admin2, true, Meta("renamed", isPublic: true), Query("new"), TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal("renamed", (await _service.GetAsync(id, _admin2, true, TestContext.Current.CancellationToken))!.Summary.Name);
        Assert.True((await _service.DeleteAsync(id, _admin2, true, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Null(await _service.GetAsync(id, _admin, true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OthersPrivate_IsTreatedAsNotFound_ForUpdateDeleteOverwrite()
    {
        var id = await CreateAsync(_user1, false, "mine");

        foreach (var (userId, isAdmin) in new[] { (_user2, false), (_admin, true) })
        {
            Assert.Equal("Error.SavedSearch.NotFound", (await _service.UpdateAsync(id, userId, isAdmin, Meta("x"), null, TestContext.Current.CancellationToken)).Errors.Single().Key);
            Assert.Equal("Error.SavedSearch.NotFound", (await _service.DeleteAsync(id, userId, isAdmin, TestContext.Current.CancellationToken)).Errors.Single().Key);
            Assert.Equal("Error.SavedSearch.NotFound", (await _service.OverwriteQueryAsync(id, userId, isAdmin, Query(), TestContext.Current.CancellationToken)).Errors.Single().Key);
        }

        Assert.NotNull(await _service.GetAsync(id, _user1, false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Names_AreUniquePerScope()
    {
        await CreateAsync(_user1, false, "dup");
        await CreateAsync(_user2, false, "dup");                       // 別ユーザーの非公開とは重複可
        await CreateAsync(_admin, true, "dup", isPublic: true);          // 非公開と公開も重複可

        Assert.False((await _service.CreateAsync(_user1, false, Meta("dup"), Query(), TestContext.Current.CancellationToken)).Result.Succeeded);
        Assert.False((await _service.CreateAsync(_admin2, true, Meta("dup", isPublic: true), Query(), TestContext.Current.CancellationToken)).Result.Succeeded);
    }

    [Fact]
    public async Task Update_RenameToExistingName_Fails_SameNameOnSelfIsFine()
    {
        var a = await CreateAsync(_user1, false, "a");
        await CreateAsync(_user1, false, "b");

        Assert.False((await _service.UpdateAsync(a, _user1, false, Meta("b"), null, TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await _service.UpdateAsync(a, _user1, false, Meta("a", pinned: true, description: "memo"), null, TestContext.Current.CancellationToken)).Succeeded);
        var detail = (await _service.GetAsync(a, _user1, false, TestContext.Current.CancellationToken))!;
        Assert.True(detail.Summary.IsPinned);
        Assert.Equal("memo", detail.Summary.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankName_Fails(string name)
    {
        Assert.False((await _service.CreateAsync(_user1, false, Meta(name), Query(), TestContext.Current.CancellationToken)).Result.Succeeded);
    }

    [Fact]
    public async Task Create_TooLongNameOrDescription_Fails()
    {
        Assert.False((await _service.CreateAsync(_user1, false, Meta(new string('a', 101)), Query(), TestContext.Current.CancellationToken)).Result.Succeeded);
        Assert.True((await _service.CreateAsync(_user1, false, Meta(new string('a', 100)), Query(), TestContext.Current.CancellationToken)).Result.Succeeded);
        Assert.False((await _service.CreateAsync(_user1, false, Meta("d", description: new string('x', 501)), Query(), TestContext.Current.CancellationToken)).Result.Succeeded);
    }

    [Fact]
    public async Task Create_InvalidQueries_AreRejected()
    {
        SavedQuery Empty() => new(SearchOperator.And, [new SearchCondition { Field = SearchField.FileName, Text = "  " }], null, null, SearchSortKey.Path, false);
        var tooMany = new SavedQuery(
            SearchOperator.And,
            [.. Enumerable.Range(0, 11).Select(i => new SearchCondition { Field = SearchField.FileName, Text = $"t{i}" })],
            null, null, SearchSortKey.Path, false);
        var tooLong = Query(new string('x', 201));

        Assert.False((await _service.CreateAsync(_user1, false, Meta("e"), Empty(), TestContext.Current.CancellationToken)).Result.Succeeded);
        Assert.False((await _service.CreateAsync(_user1, false, Meta("m"), tooMany, TestContext.Current.CancellationToken)).Result.Succeeded);
        Assert.False((await _service.CreateAsync(_user1, false, Meta("l"), tooLong, TestContext.Current.CancellationToken)).Result.Succeeded);
        Assert.Empty(await _service.ListAsync(_user1, false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FolderOnlyQuery_IsAllowed()
    {
        var folderOnly = new SavedQuery(SearchOperator.And, [], 3, "C:\\data", SearchSortKey.Name, true);

        var result = await _service.CreateAsync(_user1, false, Meta("folder"), folderOnly, TestContext.Current.CancellationToken);

        Assert.True(result.Result.Succeeded);
        var query = (await _service.GetAsync(result.Id!.Value, _user1, false, TestContext.Current.CancellationToken))!.Query!;
        Assert.Equal(3, query.FolderId);
        Assert.Equal("C:\\data", query.FolderPath);
        Assert.Equal(SearchSortKey.Name, query.Sort);
        Assert.True(query.Descending);
    }

    [Fact]
    public async Task Query_RoundTrips_AndIsNormalized()
    {
        var query = new SavedQuery(
            SearchOperator.Or,
            [
                new SearchCondition { Field = SearchField.FileName, Text = "  abc  " },
                new SearchCondition { Field = SearchField.Extension, Text = "" },
                new SearchCondition { Field = SearchField.ModifiedDate, From = new DateTime(2026, 1, 2, 13, 0, 0), To = new DateTime(2026, 3, 4) },
            ],
            5, null, SearchSortKey.Size, true);

        var id = (await _service.CreateAsync(_user1, false, Meta("q"), query, TestContext.Current.CancellationToken)).Id!.Value;
        var loaded = (await _service.GetAsync(id, _user1, false, TestContext.Current.CancellationToken))!.Query!;

        Assert.Equal(SearchOperator.Or, loaded.Operator);
        Assert.Equal(2, loaded.Conditions.Count);
        Assert.Equal("abc", loaded.Conditions[0].Text);
        Assert.Equal(SearchField.ModifiedDate, loaded.Conditions[1].Field);
        Assert.Equal(new DateTime(2026, 1, 2), loaded.Conditions[1].From);
        Assert.Equal(new DateTime(2026, 3, 4), loaded.Conditions[1].To);
        Assert.Equal(5, loaded.FolderId);
        Assert.Equal(SearchSortKey.Size, loaded.Sort);
        Assert.True(loaded.Descending);
    }

    [Fact]
    public async Task Overwrite_ReplacesOnlyTheQuery()
    {
        var id = await CreateAsync(_user1, false, "keep", pinned: true);

        Assert.True((await _service.OverwriteQueryAsync(id, _user1, false, Query("changed", SearchOperator.Or), TestContext.Current.CancellationToken)).Succeeded);

        var detail = (await _service.GetAsync(id, _user1, false, TestContext.Current.CancellationToken))!;
        Assert.Equal("keep", detail.Summary.Name);
        Assert.True(detail.Summary.IsPinned);
        Assert.Equal("changed", detail.Query!.Conditions.Single().Text);
        Assert.Equal(SearchOperator.Or, detail.Query.Operator);
        Assert.False((await _service.OverwriteQueryAsync(id, _user1, false, Query("  "), TestContext.Current.CancellationToken)).Succeeded);
    }

    [Fact]
    public async Task Visibility_AdminCanPublishOwnPrivate_OnlyOwnerCanMakePublicPrivateAgain()
    {
        var id = await CreateAsync(_admin, true, "mine");

        Assert.True((await _service.UpdateAsync(id, _admin, true, Meta("mine", isPublic: true), null, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Single(await _service.ListAsync(_user1, false, TestContext.Current.CancellationToken));

        var byOtherAdmin = await _service.UpdateAsync(id, _admin2, true, Meta("mine", isPublic: false), null, TestContext.Current.CancellationToken);
        Assert.False(byOtherAdmin.Succeeded);
        Assert.Equal("Error.SavedSearch.UnpublishCreatorOnly", byOtherAdmin.Errors.Single().Key);
        Assert.Single(await _service.ListAsync(_user1, false, TestContext.Current.CancellationToken));

        Assert.True((await _service.UpdateAsync(id, _admin, true, Meta("mine", isPublic: false), null, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Empty(await _service.ListAsync(_user1, false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonAdmin_CannotPublishOwnPrivate()
    {
        var id = await CreateAsync(_user1, false, "mine");

        var result = await _service.UpdateAsync(id, _user1, false, Meta("mine", isPublic: true), null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(await _service.ListAsync(_user2, false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Publishing_ChecksNameConflictWithExistingPublic()
    {
        await CreateAsync(_admin, true, "same", isPublic: true);
        var id = await CreateAsync(_admin2, true, "same");

        var result = await _service.UpdateAsync(id, _admin2, true, Meta("same", isPublic: true), null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task List_PinnedFirst_ThenByName()
    {
        await CreateAsync(_user1, false, "b");
        await CreateAsync(_user1, false, "a");
        await CreateAsync(_user1, false, "z", pinned: true);
        await CreateAsync(_admin, true, "y-public", isPublic: true, pinned: true);

        var names = (await _service.ListAsync(_user1, false, TestContext.Current.CancellationToken)).Select(s => s.Name);

        Assert.Equal(["y-public", "z", "a", "b"], names);
    }

    [Fact]
    public async Task PrivateLimit_IsPerUser()
    {
        for (var i = 0; i < SavedSearchLimits.MaxPrivatePerUser; i++)
        {
            await CreateAsync(_user1, false, $"s{i:D3}");
        }

        var over = await _service.CreateAsync(_user1, false, Meta("one-too-many"), Query(), TestContext.Current.CancellationToken);
        var otherUser = await _service.CreateAsync(_user2, false, Meta("fine"), Query(), TestContext.Current.CancellationToken);

        Assert.False(over.Result.Succeeded);
        var limit = over.Result.Errors.Single();
        Assert.Equal("Error.SavedSearch.PrivateLimit", limit.Key);
        Assert.Equal([SavedSearchLimits.MaxPrivatePerUser], limit.Args);
        Assert.True(otherUser.Result.Succeeded);
    }

    [Fact]
    public async Task BrokenStoredJson_IsReportedAsNullQuery_NotAnException()
    {
        var id = await CreateAsync(_user1, false, "broken");
        var db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.SavedSearches.Where(s => s.Id == id).ExecuteUpdateAsync(set => set.SetProperty(s => s.QueryJson, "{not json"), TestContext.Current.CancellationToken);

        var detail = await _service.GetAsync(id, _user1, false, TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Null(detail.Query);
        Assert.Single(await _service.ListAsync(_user1, false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_RemovesOnlyThatBookmark()
    {
        var a = await CreateAsync(_user1, false, "a");
        await CreateAsync(_user1, false, "b");

        Assert.True((await _service.DeleteAsync(a, _user1, false, TestContext.Current.CancellationToken)).Succeeded);

        Assert.Equal(["b"], (await _service.ListAsync(_user1, false, TestContext.Current.CancellationToken)).Select(s => s.Name));
        Assert.False((await _service.DeleteAsync(a, _user1, false, TestContext.Current.CancellationToken)).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Version\":99,\"Operator\":\"And\",\"Conditions\":[],\"Sort\":\"Path\",\"Descending\":false}")]
    [InlineData("{\"Version\":1,\"Operator\":\"Bogus\",\"Conditions\":[],\"Sort\":\"Path\",\"Descending\":false}")]
    [InlineData("{\"Version\":1,\"Operator\":1,\"Conditions\":[],\"Sort\":\"Path\",\"Descending\":false}")]
    public void SavedQueryJson_RejectsUnreadableOrUnknownData(string json)
    {
        Assert.Null(SavedQueryJson.TryDeserialize(json));
    }

    [Fact]
    public void SavedQueryJson_WritesVersionAndReadableEnumNames()
    {
        var json = SavedQueryJson.Serialize(Query("abc", SearchOperator.Or));

        Assert.Contains("\"Version\":1", json);
        Assert.Contains("\"Operator\":\"Or\"", json);
        Assert.Contains("\"Field\":\"FileName\"", json);
        Assert.DoesNotContain("FolderId", json);
    }
}
