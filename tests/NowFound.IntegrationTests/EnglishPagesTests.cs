using System.Text.RegularExpressions;

namespace NowFound.IntegrationTests;

/// <summary>英語で表示したとき、どの画面にも日本語（翻訳漏れ）が残らない。</summary>
public sealed partial class EnglishPagesTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [GeneratedRegex("[\u3040-\u30ff\u4e00-\u9fff]+")]
    private static partial Regex JapaneseRegex();

    // 言語切替ボタンの「日本語」は、その言語自身の表記なので許可する。
    private static string[] Japanese(string html) =>
        [.. JapaneseRegex().Matches(html).Select(m => m.Value).Where(v => v != "日本語").Distinct()];

    public static TheoryData<string> AdminPages() =>
    [
        "/", "/SavedSearches", "/Users", "/Users/Create", "/Groups", "/Groups/Create", "/Groups/Edit/1",
        "/Folders", "/Folders/Create", "/Logs", "/Settings", "/Settings/ExcludeDefaults", "/Settings/FileKinds",
        "/Account/Preferences", "/Account/AccessDenied", "/Information",
    ];

    [Theory]
    [MemberData(nameof(AdminPages))]
    public async Task AdminPage_InEnglish_HasNoJapaneseText(string url)
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();
        await client.SetLanguageAsync("en");

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode}");
        Assert.Empty(Japanese(html));
    }

    [GeneratedRegex(@"<input[^>]*name=""(?<name>Rows\[\d+\]\.\w+)""[^>]*value=""(?<value>[^""]*)""")]
    private static partial Regex RowInputRegex();

    [GeneratedRegex(@"<textarea[^>]*name=""(?<name>Rows\[\d+\]\.Extensions)""[^>]*>(?<value>.*?)</textarea>", RegexOptions.Singleline)]
    private static partial Regex RowTextareaRegex();

    [Fact]
    public async Task FileKinds_SavedUnchangedInEnglish_StayBuiltIn_AndAreTranslatedBackToJapanese()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();
        await client.SetLanguageAsync("en");

        var editor = await client.GetStringAsync("/Settings/FileKinds", TestContext.Current.CancellationToken);
        Assert.Contains("value=\"Folder\"", editor);
        var fields = RowInputRegex().Matches(editor).Select(m => (m.Groups["name"].Value, System.Net.WebUtility.HtmlDecode(m.Groups["value"].Value)))
            .Concat(RowTextareaRegex().Matches(editor).Select(m => (m.Groups["name"].Value, System.Net.WebUtility.HtmlDecode(m.Groups["value"].Value.Trim()))))
            .Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2));
        var saved = await client.PostFormAsync("/Settings/FileKinds", "/Settings/FileKinds", fields);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, saved.StatusCode);

        await client.SetLanguageAsync("ja");
        var japanese = await client.GetStringAsync("/Settings/FileKinds", TestContext.Current.CancellationToken);
        Assert.Contains("value=\"フォルダ\"", japanese);
        Assert.DoesNotContain("value=\"Folder\"", japanese);
    }

    [Fact]
    public async Task LoginPage_InEnglish_HasNoJapaneseText()
    {
        var client = _factory.CreateBrowserLikeClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
        request.Headers.AcceptLanguage.ParseAdd("en");

        var html = await (await client.SendAsync(request, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Empty(Japanese(html));
    }

    [Fact]
    public async Task Logs_InEnglish_TranslateOperationDetails_ForTheAdminsLanguage()
    {
        var client = _factory.CreateBrowserLikeClient();
        await client.LoginAsAdminAsync();
        await client.PostFormAsync("/Users/Create", "/Users/Create", [new("Email", "x@example.com"), new("Password", "Passw0rd!")]);
        await client.SetLanguageAsync("en");

        var html = await client.GetStringAsync("/Logs", TestContext.Current.CancellationToken);

        Assert.Contains("x@example.com", html);
        Assert.Empty(Japanese(html));
    }
}
