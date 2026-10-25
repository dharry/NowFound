using System.Net;

namespace NowFound.IntegrationTests;

/// <summary>検索画面・保存した検索（ブックマーク）が英語の表示言語で描画されること。</summary>
public sealed class SearchLocalizationTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminAsync(string language)
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        return admin;
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string name, bool isPublic) =>
        client.PostFormAsync("/SavedSearches/Create", "/SavedSearches",
        [
            new("Name", name),
            new("IsPublic", isPublic ? "true" : "false"),
            new("Operator", "And"),
            new("Sort", "Path"),
            new("Desc", "false"),
            new("Conditions[0].Field", "FileName"),
            new("Conditions[0].Text", "report"),
        ]);

    [Fact]
    public async Task SearchPage_IsInEnglish()
    {
        var admin = await AdminAsync("en");

        var page = await admin.GetStringAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=abc&Conditions[0].SizeValue=-1", TestContext.Current.CancellationToken);

        Assert.Contains("<title>Search", page);
        Assert.Contains(">Side panel</button>", page);
        Assert.Contains(">Advanced</button>", page);
        Assert.Contains("★ Save", page);
        Assert.Contains("Save the current search as a bookmark", page);
        Assert.Contains(">File name</option>", page);
        Assert.Contains(">Modified date</option>", page);
        Assert.Contains("≥ at least</option>", page);
        Assert.Contains("AND (match all)", page);
        Assert.Contains("Add condition", page);
        Assert.Contains(">Folders</button>", page);
        Assert.Contains("0 results", page);
        Assert.Contains("No matching files.", page);
        Assert.Contains("Save search", page);
        // 「管理者だけが公開できる」の注記は一般ユーザーにだけ出る（ここは管理者）。
        Assert.DoesNotContain("Only administrators can make a bookmark public.", page);
    }

    [Fact]
    public async Task SearchPage_LeavesNoJapaneseUiText_InEnglish()
    {
        var admin = await AdminAsync("en");

        var page = await admin.GetStringAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=abc", TestContext.Current.CancellationToken);

        foreach (var japanese in new[] { "サイドパネル", "詳細条件", "条件を追加", "該当するファイルはありません", "表示する列", "検索を保存", "キャンセル", "コピー", "更新日時" })
        {
            Assert.DoesNotContain(japanese, page);
        }
    }

    [Fact]
    public async Task SearchPage_StaysJapanese_ByDefault()
    {
        var admin = await AdminAsync("ja");

        var page = await admin.GetStringAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=abc", TestContext.Current.CancellationToken);

        Assert.Contains("サイドパネル", page);
        Assert.Contains("該当するファイルはありません。", page);
        Assert.Contains(">ファイル名</option>", page);
    }

    [Fact]
    public async Task InvalidSizeWarning_IsInEnglish()
    {
        var admin = await AdminAsync("en");

        var page = await admin.GetStringAsync(
            "/?Conditions[0].Field=Size&Conditions[0].SizeComparison=GreaterOrEqual&Conditions[0].SizeValue=-5&Conditions[0].SizeUnit=KB",
            TestContext.Current.CancellationToken);

        Assert.Contains("Specify the size as a number of 0 or more.", page);
        Assert.DoesNotContain("サイズは 0 以上", page);
    }

    [Fact]
    public async Task SearchPage_EmbedsEnglishScriptMessages()
    {
        var admin = await AdminAsync("en");

        var page = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("\"Js.Copy.Done\":\"Copied\"", page);
        Assert.Contains("\"Js.Tree.Expand\":\"Expand\"", page);
        Assert.DoesNotContain("コピーしました", page);
    }

    [Fact]
    public async Task SavedSearches_FlashListAndConfirm_AreInEnglish()
    {
        var admin = await AdminAsync("en");

        var saved = await SaveAsync(admin, "Monthly", isPublic: true);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var banner = await admin.GetStringAsync(saved.Headers.Location!.ToString(), TestContext.Current.CancellationToken);
        Assert.Contains("Saved &quot;Monthly&quot;.", banner);
        Assert.Contains("Saved search:", banner);
        Assert.Contains(">Overwrite</button>", banner);

        var list = await admin.GetStringAsync("/SavedSearches", TestContext.Current.CancellationToken);
        Assert.Contains("<h1>Saved searches</h1>", list);
        Assert.Contains("Public bookmarks (all users can run them)", list);
        Assert.Contains("You have no private bookmarks.", list);
        Assert.Contains("Delete the public bookmark &quot;Monthly&quot;?", list);
        Assert.Contains(">Run</a>", list);
        Assert.DoesNotContain("実行", list);
    }

    [Fact]
    public async Task SavedSearches_EditPageAndMissingBookmark_AreInEnglish()
    {
        var admin = await AdminAsync("en");
        var saved = await SaveAsync(admin, "Mine", isPublic: false);
        var location = saved.Headers.Location!.ToString();
        var id = System.Text.RegularExpressions.Regex.Match(location, @"Saved=(\d+)").Groups[1].Value;

        var edit = await admin.GetStringAsync($"/SavedSearches/Edit?id={id}", TestContext.Current.CancellationToken);
        Assert.Contains("<h1>Edit bookmark</h1>", edit);
        Assert.Contains("Private (creator only)", edit);
        Assert.Contains("Pin to the top of the list", edit);
        Assert.DoesNotContain("ブックマークの編集", edit);

        var missing = await admin.GetAsync("/SavedSearches/Run?id=99999", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, missing.StatusCode);
        var page = await admin.GetStringAsync(missing.Headers.Location!.ToString(), TestContext.Current.CancellationToken);
        Assert.Contains("The bookmark was not found.", page);
    }
}
