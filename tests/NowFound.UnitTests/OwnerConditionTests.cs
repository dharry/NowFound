using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;
using NowFound.Web.Models;
using NowFound.Web.Services;

namespace NowFound.UnitTests;

public sealed class OwnerConditionTests : IAsyncLifetime
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mk-owner"));

    private TestHost _host = null!;
    private IServiceScope _scope = null!;
    private ISearchService _search = null!;
    private string _adminId = null!;
    private int _folderId;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _scope = _host.Services.CreateScope();
        var sp = _scope.ServiceProvider;
        _search = sp.GetRequiredService<ISearchService>();
        _adminId = (await sp.GetRequiredService<IUserAdminService>().ListAsync()).Single(u => u.IsAdmin).Id;

        var settings = sp.GetRequiredService<IAdminSettingsService>();
        await settings.SaveFolderAsync(new FolderEdit { Name = "Owner", Path = Root });
        _folderId = (await settings.ListFoldersAsync()).Single().Id;

        var seeder = new FilesSeeder(sp.GetRequiredService<FilesDbContext>());
        await seeder.AddAsync(_folderId, Root, "a.txt", "txt", 1, owner: @"CORP\Alice");
        await seeder.AddAsync(_folderId, Root, "b.txt", "txt", 1, owner: @"CORP\Bob");
        await seeder.AddAsync(_folderId, Root, "c.txt", "txt", 1, owner: "alice_100%");
        await seeder.AddAsync(_folderId, Root, "d.txt", "txt", 1);   // オーナー不明
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<List<string>> NamesAsync(string text)
    {
        var condition = new SearchCondition { Field = SearchField.Owner, Text = text };
        var page = await _search.SearchAsync(new SearchRequest(_adminId, true, [condition], SearchOperator.And, _folderId, null, 1));
        return page.Items.Select(i => i.Name).Order().ToList();
    }

    [Fact]
    public async Task PartialMatch_IsCaseInsensitive() =>
        Assert.Equal(["a.txt", "c.txt"], await NamesAsync("alice"));

    [Fact]
    public async Task ShortKeyword_Works() =>
        Assert.Equal(["b.txt"], await NamesAsync("Bo"));

    [Fact]
    public async Task LikeWildcards_AreTreatedAsLiteralCharacters()
    {
        Assert.Equal(["c.txt"], await NamesAsync("100%"));
        Assert.Empty(await NamesAsync("a%e"));
        Assert.Equal(["a.txt", "b.txt"], await NamesAsync(@"CORP\"));
    }

    [Fact]
    public async Task EntriesWithoutOwner_NeverMatch() =>
        Assert.DoesNotContain("d.txt", await NamesAsync("a"));

    [Fact]
    public async Task EmptyText_IsIgnored()
    {
        var condition = new SearchCondition { Field = SearchField.Owner, Text = "  " };
        Assert.True(condition.IsEmpty);
        var page = await _search.SearchAsync(new SearchRequest(_adminId, true, [condition], SearchOperator.And, _folderId, null, 1), TestContext.Current.CancellationToken);
        Assert.Equal(4, page.TotalCount);
    }

    [Fact]
    public void FieldLabels_AreInTheRequestedOrder() =>
        Assert.Equal(
            ["すべて", "ファイル名", "フォルダ名", "サイズ", "拡張子", "オーナー", "更新日", "作成日"],
            SearchLocalizerTestHelper.Japanese(localizer => SearchLabels.FieldKeys.Values.Select(key => localizer[key].Value).ToArray()));

    [Fact]
    public void Describer_IncludesOwnerKeyword() =>
        Assert.Contains("Owner='bob'", SearchLocalizerTestHelper.Japanese(localizer => SearchDescriber.Describe(
            [new SearchCondition { Field = SearchField.Owner, Text = "bob" }], SearchOperator.And, null, null, localizer)));

    [Fact]
    public void SavedQuery_RoundTripsOwnerCondition()
    {
        var query = new SavedQuery(
            SearchOperator.And, [new SearchCondition { Field = SearchField.Owner, Text = "bob" }], null, null, SearchSortKey.Path, false);
        var json = SavedQueryJson.Serialize(query);
        Assert.Contains("\"Owner\"", json);
        var back = SavedQueryJson.TryDeserialize(json);
        Assert.Equal(SearchField.Owner, back!.Conditions.Single().Field);
        Assert.Equal("bob", back.Conditions.Single().Text);
    }
}
