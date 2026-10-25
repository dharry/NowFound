namespace NowFound.IntegrationTests;

/// <summary>ユーザー管理・グループ管理・検索対象フォルダ・ブックマーク・操作ログの、検索フォームと列見出しの並べ替え。</summary>
public sealed class ListSearchSortFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        return admin;
    }

    private static Task<string> GetAsync(HttpClient client, string url) =>
        client.GetStringAsync(url, TestContext.Current.CancellationToken);

    private static int IndexOf(string html, string text) => html.IndexOf(text, StringComparison.Ordinal);

    [Fact]
    public async Task Users_SearchByKeywordAndRole_AndSortByColumns()
    {
        var admin = await AdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create", [new("Email", "bob@example.com"), new("Password", "Passw0rd!")]);
        await admin.PostFormAsync("/Users/Create", "/Users/Create", [new("Email", "amy@example.com"), new("Password", "Passw0rd!"), new("IsAdmin", "true")]);

        var page = await GetAsync(admin, "/Users");
        Assert.Contains("name=\"Q\"", page);
        Assert.Contains("name=\"Role\"", page);
        Assert.Contains("href=\"/Users?Sort=role\"", page.Replace("&amp;", "&"));
        Assert.Contains("aria-sort=\"ascending\"", page); // 既定はメールアドレス順

        var found = await GetAsync(admin, "/Users?Q=BOB");
        Assert.Contains("bob@example.com", found);
        Assert.DoesNotContain("amy@example.com", found);

        var admins = await GetAsync(admin, "/Users?Role=admin");
        Assert.Contains("amy@example.com", admins);
        Assert.DoesNotContain("bob@example.com", admins);

        var descending = await GetAsync(admin, "/Users?Sort=email&Desc=true");
        Assert.True(IndexOf(descending, "bob@example.com") < IndexOf(descending, "amy@example.com"));
        Assert.Contains("aria-sort=\"descending\"", descending);

        // 検索しても並べ替えは保たれ、クリアで条件が外れる。
        Assert.Contains("name=\"Sort\" value=\"email\"", await GetAsync(admin, "/Users?Sort=email&Desc=true&Q=a"));
        Assert.Contains("href=\"/Users\"", page);
    }

    [Fact]
    public async Task Groups_AndFolders_SearchAndSort()
    {
        var admin = await AdminAsync();
        await admin.PostFormAsync("/Groups/Save", "/Groups/Create", [new("Name", "Zeta"), new("Description", "red team")]);
        await admin.PostFormAsync("/Groups/Save", "/Groups/Create", [new("Name", "Eta"), new("Description", "blue team")]);
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "Alpha"), new("Path", Path.Combine(root, "zz1"))]);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "Beta"), new("Path", Path.Combine(root, "aa1"))]);

        var groups = await GetAsync(admin, "/Groups?Q=blue");
        Assert.Contains("Eta", groups);
        Assert.DoesNotContain("Zeta", groups);
        var groupsByName = await GetAsync(admin, "/Groups?Sort=name&Desc=true");
        Assert.True(IndexOf(groupsByName, "Zeta") < IndexOf(groupsByName, "Eta"));

        var folders = await GetAsync(admin, "/Folders?Q=aa1");
        Assert.Contains("Beta", folders);
        Assert.DoesNotContain("Alpha", folders);
        var foldersByPath = await GetAsync(admin, "/Folders?Sort=path");
        Assert.True(IndexOf(foldersByPath, "Beta") < IndexOf(foldersByPath, "Alpha"));
        Assert.Contains("Sort=excludes", (await GetAsync(admin, "/Folders")).Replace("&amp;", "&"));
    }

    [Fact]
    public async Task Bookmarks_SearchByKeywordAndVisibility_AndSort()
    {
        var admin = await AdminAsync();
        foreach (var (name, isPublic) in new[] { ("alpha-private", "false"), ("beta-public", "true") })
        {
            await admin.PostFormAsync("/SavedSearches/Create", "/SavedSearches",
            [
                new("Name", name), new("IsPublic", isPublic), new("Operator", "And"), new("Sort", "Path"), new("Desc", "false"),
                new("Conditions[0].Field", "FileName"), new("Conditions[0].Text", "x"),
            ]);
        }

        var search = await GetAsync(admin, "/SavedSearches?Q=beta");
        Assert.Contains("beta-public", search);
        Assert.DoesNotContain("alpha-private", search);

        var mine = await GetAsync(admin, "/SavedSearches?Visibility=mine");
        Assert.Contains("alpha-private", mine);
        Assert.DoesNotContain("beta-public", mine);

        var sorted = await GetAsync(admin, "/SavedSearches?Sort=name&Desc=true");
        // 自分の分と公開の分は別の表。どちらも、並べ替えた列が降順になり、列見出しのリンクがある。
        Assert.Contains("aria-sort=\"descending\"", sorted);
        Assert.Contains("Sort=updated", (await GetAsync(admin, "/SavedSearches")).Replace("&amp;", "&"));
        Assert.Contains("Sort=owner", (await GetAsync(admin, "/SavedSearches")).Replace("&amp;", "&"));
        Assert.Contains("name=\"Visibility\"", await GetAsync(admin, "/SavedSearches"));
    }

    [Fact]
    public async Task OperationLog_HasSortableHeaders_AndKeepsTheSortWhenSearching()
    {
        var admin = await AdminAsync();

        var page = (await GetAsync(admin, "/Logs")).Replace("&amp;", "&");
        Assert.Contains("Sort=user", page);
        Assert.Contains("Sort=operation", page);
        Assert.Contains("Sort=ip", page);
        Assert.Contains("aria-sort=\"descending\"", page); // 既定は日時の新しい順

        var sorted = await GetAsync(admin, "/Logs?Sort=user&Desc=true");
        Assert.Contains("name=\"Sort\" value=\"user\"", sorted);
        Assert.Contains("name=\"Desc\" value=\"true\"", sorted);
        Assert.Contains("aria-sort=\"descending\"", sorted);
    }
}
