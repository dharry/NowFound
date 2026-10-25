using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

public sealed class SearchServiceTests : IAsyncLifetime
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mk-search"));
    private static readonly string OtherRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mk-other"));

    private TestHost _host = null!;
    private IServiceScope _scope = null!;
    private ISearchService _search = null!;
    private string _adminId = null!;
    private string _memberId = null!;
    private string _outsiderId = null!;
    private int _folderId;
    private int _otherFolderId;

    private static string P(string root, params string[] parts) => Path.Combine([root, .. parts]);

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _scope = _host.Services.CreateScope();
        var sp = _scope.ServiceProvider;
        _search = sp.GetRequiredService<ISearchService>();
        var users = sp.GetRequiredService<IUserAdminService>();
        var settings = sp.GetRequiredService<IAdminSettingsService>();

        await users.CreateAsync(new CreateUserRequest("member@example.com", "Passw0rd!", false, false));
        await users.CreateAsync(new CreateUserRequest("outsider@example.com", "Passw0rd!", false, false));
        var all = await users.ListAsync();
        _adminId = all.Single(u => u.IsAdmin).Id;
        _memberId = all.Single(u => u.Email == "member@example.com").Id;
        _outsiderId = all.Single(u => u.Email == "outsider@example.com").Id;

        await settings.SaveFolderAsync(new FolderEdit { Name = "Main", Path = Root });
        await settings.SaveFolderAsync(new FolderEdit { Name = "Other", Path = OtherRoot });
        var folders = await settings.ListFoldersAsync();
        _folderId = folders.Single(f => f.Name == "Main").Id;
        _otherFolderId = folders.Single(f => f.Name == "Other").Id;
        await settings.SaveGroupAsync(new GroupEdit { Name = "G", UserIds = [_memberId], FolderIds = [_folderId] });

        var seeder = new FilesSeeder(sp.GetRequiredService<FilesDbContext>());
        await seeder.AddAsync(_folderId, Root, "report-2026.xlsx", "xlsx", 100, Utc(2026, 1, 10), "alice");
        await seeder.AddAsync(_folderId, Root, "memo.txt", "txt", 5, Utc(2026, 3, 1), "bob");
        await seeder.AddAsync(_folderId, P(Root, "reports"), "summary.docx", "docx", 50, Utc(2026, 2, 1), "alice");
        await seeder.AddAsync(_folderId, P(Root, "reports", "old"), "a.txt", "txt", 1, Utc(2025, 5, 1));
        await seeder.AddAsync(_folderId, Root, "reports", isDirectory: true);
        await seeder.AddAsync(_folderId, P(Root, "reports"), "old", isDirectory: true);
        await seeder.AddAsync(_otherFolderId, OtherRoot, "secret-report.txt", "txt", 9, Utc(2026, 4, 1));
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    private static SearchCondition C(SearchField field, string? text = null, DateTime? from = null, DateTime? to = null) =>
        new() { Field = field, Text = text, From = from, To = to };

    private async Task<List<string>> NamesAsync(
        string userId, bool isAdmin, SearchOperator op, int? folderId = null, string? path = null, params SearchCondition[] conditions)
    {
        var page = await _search.SearchAsync(new SearchRequest(userId, isAdmin, conditions, op, folderId, path, 1));
        return page.Items.Select(i => i.Name).OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task Admin_NoConditions_SeesAllFolders()
    {
        Assert.Equal(7, (await NamesAsync(_adminId, true, SearchOperator.And)).Count);
    }

    [Fact]
    public async Task Member_SeesOnlyAssignedFolders_Outsider_SeesNothing()
    {
        var member = await NamesAsync(_memberId, false, SearchOperator.And, null, null, C(SearchField.FileName, "report"));
        Assert.DoesNotContain("secret-report.txt", member);
        Assert.Contains("report-2026.xlsx", member);

        Assert.Empty(await NamesAsync(_outsiderId, false, SearchOperator.And));
        Assert.Empty(await _search.GetAccessibleFoldersAsync(_outsiderId, false, TestContext.Current.CancellationToken));
        Assert.Single(await _search.GetAccessibleFoldersAsync(_memberId, false, TestContext.Current.CancellationToken));
        Assert.Equal(2, (await _search.GetAccessibleFoldersAsync(_adminId, true, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task Member_RequestingForbiddenFolder_ReturnsEmpty()
    {
        Assert.Empty(await NamesAsync(_memberId, false, SearchOperator.And, _otherFolderId));
        Assert.Empty(await _search.GetSubFoldersAsync(_memberId, false, _otherFolderId, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileName_Fts_PartialMatchIsCaseInsensitive()
    {
        var names = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.FileName, "REPORT"));

        Assert.Equal(["report-2026.xlsx", "reports", "secret-report.txt"], names);
    }

    [Fact]
    public async Task FileName_ShortTerm_UsesLike()
    {
        var names = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.FileName, "me"));

        Assert.Equal(["memo.txt"], names);
    }

    [Fact]
    public async Task FileName_QuoteAndLikeWildcardsAreTreatedLiterally()
    {
        Assert.Empty(await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.FileName, "\"report")));
        Assert.Empty(await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.FileName, "%")));
        Assert.Empty(await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.FileName, "_")));
    }

    [Fact]
    public async Task FolderName_MatchesFolderPath()
    {
        var names = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.FolderName, "reports"));

        Assert.Equal(["a.txt", "old", "summary.docx"], names);
    }

    [Fact]
    public async Task Extension_AcceptsMultipleWithDots()
    {
        var names = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.Extension, ".XLSX, docx"));

        Assert.Equal(["report-2026.xlsx", "summary.docx"], names);
    }

    [Fact]
    public async Task All_MatchesNameFolderAndExtension()
    {
        var byExt = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.All, "docx"));
        var byFolder = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.All, "old"));

        Assert.Equal(["summary.docx"], byExt);
        Assert.Equal(["a.txt", "old"], byFolder);
    }

    [Fact]
    public async Task DateRange_IsInclusiveOfEndDay()
    {
        var names = await NamesAsync(_adminId, true, SearchOperator.And, null, null,
            C(SearchField.ModifiedDate, from: new DateTime(2026, 1, 10), to: new DateTime(2026, 2, 1)));

        Assert.Equal(["report-2026.xlsx", "summary.docx"], names);
    }

    [Fact]
    public async Task And_NarrowsAndOr_Widens()
    {
        SearchCondition[] conditions = [C(SearchField.FileName, "report"), C(SearchField.Extension, "txt")];

        var and = await NamesAsync(_adminId, true, SearchOperator.And, null, null, conditions);
        var or = await NamesAsync(_adminId, true, SearchOperator.Or, null, null, conditions);

        Assert.Equal(["secret-report.txt"], and);
        Assert.Equal(["a.txt", "memo.txt", "report-2026.xlsx", "reports", "secret-report.txt"], or);
    }

    [Fact]
    public async Task EmptyConditions_AreIgnored()
    {
        var names = await NamesAsync(_adminId, true, SearchOperator.And, _folderId, null,
            C(SearchField.FileName, "  "), C(SearchField.ModifiedDate), C(SearchField.Extension, "txt"));

        Assert.Equal(["a.txt", "memo.txt"], names);
    }

    [Fact]
    public async Task FolderScope_RestrictsToSubtree()
    {
        var names = await NamesAsync(_adminId, true, SearchOperator.And, _folderId, P(Root, "reports"));

        Assert.Equal(["a.txt", "old", "reports", "summary.docx"], names);
    }

    [Fact]
    public async Task FolderScope_OutsideRoot_ReturnsEmpty()
    {
        Assert.Empty(await NamesAsync(_adminId, true, SearchOperator.And, _folderId, P(OtherRoot)));
        Assert.Empty(await _search.GetSubFoldersAsync(_adminId, true, _folderId, P(OtherRoot), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubFolders_ListsDirectChildrenOnly()
    {
        var top = await _search.GetSubFoldersAsync(_memberId, false, _folderId, null, TestContext.Current.CancellationToken);
        var nested = await _search.GetSubFoldersAsync(_memberId, false, _folderId, P(Root, "reports"), TestContext.Current.CancellationToken);

        Assert.Equal(["reports"], top.Select(s => s.Name));
        Assert.Equal(["old"], nested.Select(s => s.Name));
        Assert.Equal(P(Root, "reports", "old"), nested.Single().Path);
    }

    private async Task<List<string>> SortedNamesAsync(SearchSortKey key, bool descending)
    {
        var page = await _search.SearchAsync(new SearchRequest(
            _adminId, true, [C(SearchField.Extension, "txt,xlsx,docx")], SearchOperator.And, _folderId, null, 1, key, descending));
        return page.Items.Select(i => i.Name).ToList();
    }

    [Fact]
    public async Task Sort_ByColumn_AscendingAndDescending()
    {
        Assert.Equal(["a.txt", "memo.txt", "report-2026.xlsx", "summary.docx"], await SortedNamesAsync(SearchSortKey.Name, false));
        Assert.Equal(["summary.docx", "report-2026.xlsx", "memo.txt", "a.txt"], await SortedNamesAsync(SearchSortKey.Name, true));
        Assert.Equal(["a.txt", "memo.txt", "summary.docx", "report-2026.xlsx"], await SortedNamesAsync(SearchSortKey.Size, false));
        Assert.Equal(["report-2026.xlsx", "summary.docx", "memo.txt", "a.txt"], await SortedNamesAsync(SearchSortKey.Size, true));
        Assert.Equal(["a.txt", "report-2026.xlsx", "summary.docx", "memo.txt"], await SortedNamesAsync(SearchSortKey.Modified, false));
        Assert.Equal(["memo.txt", "summary.docx", "report-2026.xlsx", "a.txt"], await SortedNamesAsync(SearchSortKey.Modified, true));

        var byExtension = await _search.SearchAsync(new SearchRequest(
            _adminId, true, [C(SearchField.Extension, "txt,xlsx,docx")], SearchOperator.And, _folderId, null, 1, SearchSortKey.Extension, false), TestContext.Current.CancellationToken);
        Assert.Equal(["docx", "txt", "txt", "xlsx"], byExtension.Items.Select(i => i.Extension));
    }

    [Fact]
    public async Task Sort_FolderColumn_OrdersByFolderPathThenFullPath()
    {
        var names = await SortedNamesAsync(SearchSortKey.Folder, false);

        // Root 直下 → reports → reports/old の順（フォルダパス昇順）。
        Assert.Equal(["memo.txt", "report-2026.xlsx", "summary.docx", "a.txt"], names);
    }

    [Fact]
    public async Task Owner_IsReadFromOwnersTable_AndSortableWithNullsFirst()
    {
        var page = await _search.SearchAsync(new SearchRequest(
            _adminId, true, [C(SearchField.Extension, "txt,xlsx,docx")], SearchOperator.And, _folderId, null, 1, SearchSortKey.Owner, false), TestContext.Current.CancellationToken);

        Assert.Equal(
            [("a.txt", null), ("report-2026.xlsx", "alice"), ("summary.docx", "alice"), ("memo.txt", "bob")],
            page.Items.Select(i => (i.Name, i.Owner)));

        var descending = await _search.SearchAsync(new SearchRequest(
            _adminId, true, [C(SearchField.Extension, "txt,xlsx,docx")], SearchOperator.And, _folderId, null, 1, SearchSortKey.Owner, true), TestContext.Current.CancellationToken);
        Assert.Equal("memo.txt", descending.Items.First().Name);
    }

    [Fact]
    public async Task Sort_AppliesAcrossPages()
    {
        var users = _scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await users.SetPageSizeAsync(_adminId, 2, TestContext.Current.CancellationToken);

        var page2 = await _search.SearchAsync(new SearchRequest(
            _adminId, true, [C(SearchField.Extension, "txt,xlsx,docx")], SearchOperator.And, _folderId, null, 2, SearchSortKey.Size, true), TestContext.Current.CancellationToken);

        Assert.Equal(["memo.txt", "a.txt"], page2.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Items_ExposeFullPathAndFolderIdForDrillDown()
    {
        var page = await _search.SearchAsync(new SearchRequest(
            _adminId, true, [C(SearchField.FileName, "summary")], SearchOperator.And, null, null, 1), TestContext.Current.CancellationToken);

        var item = Assert.Single(page.Items);
        Assert.Equal(_folderId, item.SearchFolderId);
        Assert.Equal(P(Root, "reports", "summary.docx"), item.FullPath);
        Assert.Equal(P(Root, "reports"), item.FolderPath);
    }

    [Fact]
    public async Task Paging_UsesResolvedPageSize()
    {
        var users = _scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await users.SetPageSizeAsync(_adminId, 3, TestContext.Current.CancellationToken);

        SearchPage Page(int p) => _search.SearchAsync(new SearchRequest(_adminId, true, [], SearchOperator.And, null, null, p)).Result;

        var first = Page(1);
        var last = Page(3);
        var beyond = Page(4);

        Assert.Equal(3, first.PageSize);
        Assert.Equal(7, first.TotalCount);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(3, first.Items.Count);
        Assert.Single(last.Items);
        Assert.Empty(beyond.Items);
        Assert.Equal(1, Page(0).Page);
    }

    [Fact]
    public async Task ResolvePageSize_UserThenDefaultThen500()
    {
        var settings = _scope.ServiceProvider.GetRequiredService<IAdminSettingsService>();
        var users = _scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        Assert.Equal(SystemSetting.DefaultPageSize, await _search.ResolvePageSizeAsync(_memberId, TestContext.Current.CancellationToken));
        await settings.SetDefaultPageSizeAsync(100, TestContext.Current.CancellationToken);
        Assert.Equal(100, await _search.ResolvePageSizeAsync(_memberId, TestContext.Current.CancellationToken));
        await users.SetPageSizeAsync(_memberId, 25, TestContext.Current.CancellationToken);
        Assert.Equal(25, await _search.ResolvePageSizeAsync(_memberId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DateRange_WithExplicitTimeZone_AppliesUtcBoundaryCorrectly()
    {
        var jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

        // report-2026.xlsx は 2026-01-10 00:00:00 UTC（JST では 2026-01-10 09:00:00 JST）
        // JST の 2026-01-10 を指定するとヒットする
        var hitRequest = new SearchRequest(
            _adminId, true,
            [new SearchCondition { Field = SearchField.ModifiedDate, From = new DateTime(2026, 1, 10), To = new DateTime(2026, 1, 10) }],
            SearchOperator.And, null, null, 1, TimeZone: jst);
        var hitResult = await _search.SearchAsync(hitRequest, TestContext.Current.CancellationToken);
        Assert.Contains(hitResult.Items, i => i.Name == "report-2026.xlsx");

        // JST の 2026-01-11 を指定すると（翌日なので）ヒットしない
        var missRequest = new SearchRequest(
            _adminId, true,
            [new SearchCondition { Field = SearchField.ModifiedDate, From = new DateTime(2026, 1, 11), To = new DateTime(2026, 1, 11) }],
            SearchOperator.And, null, null, 1, TimeZone: jst);
        var missResult = await _search.SearchAsync(missRequest, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(missResult.Items, i => i.Name == "report-2026.xlsx");
    }

    [Fact]
    public async Task All_ThreeCharactersOrMore_UsesUnion_MatchesFilesFts_DirectoriesFts_AndExtension()
    {
        // 1. ファイル名にマッチ（FilesFts 経由）
        var byName = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.All, "secret"));
        Assert.Equal(["secret-report.txt"], byName);

        // 2. フォルダパスにマッチ（DirectoriesFts 経由で配下のファイルがヒット）
        var byFolder = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.All, "reports"));
        Assert.Contains("summary.docx", byFolder);
        Assert.Contains("old", byFolder);

        // 3. 拡張子にマッチ（Extension 経由）
        var byExt = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.All, "xlsx"));
        Assert.Equal(["report-2026.xlsx"], byExt);

        // 4. ファイル名・フォルダ名の両方にヒットし重複排除されるケース
        var byBoth = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.All, "old"));
        // "old" はフォルダ名（DirectoriesFts）で a.txt が配下、かつディレクトリ自身（FilesFts）でもヒット
        Assert.Equal(["a.txt", "old"], byBoth);
    }

    [Fact]
    public async Task All_WithDotPrefix_MatchesExtensionViaUnion()
    {
        // ".txt" は長さ 4 >= 3。ext は "txt" となり、Extension = 'txt' および FilesFts MATCH '".txt"' が UNION される
        var names = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.All, ".txt"));
        Assert.Equal(["a.txt", "memo.txt", "secret-report.txt"], names);
    }

    [Fact]
    public async Task All_DotsOnly_DoesNotMatchAllFilesDueToEmptyExtension()
    {
        // "..." は長さ 3 だが TrimStart('.') で ext が空になるため、Extension への無条件一致が抑止される
        var names = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.All, "..."));
        Assert.Empty(names);
    }

    [Fact]
    public async Task All_LessThanThreeChars_FallsBackToLikeCorrectly()
    {
        // 2文字 "me" は LIKE フォールバックで memo.txt にヒット
        var names = await NamesAsync(_adminId, true, SearchOperator.And, null, null, C(SearchField.All, "me"));
        Assert.Equal(["memo.txt"], names);
    }
}
