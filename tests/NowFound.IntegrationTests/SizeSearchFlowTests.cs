using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.IntegrationTests;

public sealed partial class SizeSearchFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminWithFilesAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "Temp"), new("Path", root)]);

        await using var scope = _factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var directory = new DirectoryEntry { SearchFolderId = 1, Path = root };
        files.Directories.Add(directory);
        await files.SaveChangesAsync();

        var now = DateTime.UtcNow;
        (string Name, long? Size, bool IsDirectory)[] entries =
        [
            ("small.dat", 500, false), ("big.dat", 2048, false), ("huge.dat", 10L * 1024 * 1024, false), ("some-folder", null, true),
        ];
        files.Files.AddRange(entries.Select(e => new FileEntry
        {
            SearchFolderId = 1, DirectoryId = directory.Id, Name = e.Name, Extension = e.IsDirectory ? "" : "dat",
            IsDirectory = e.IsDirectory, Size = e.Size, ModifiedAt = now, CreatedAt = now,
        }));
        await files.SaveChangesAsync();
        return admin;
    }

    private static string Query(string comparison, string value, string unit) =>
        $"/?Conditions[0].Field=Size&Conditions[0].SizeComparison={comparison}&Conditions[0].SizeValue={value}&Conditions[0].SizeUnit={unit}";

    /// <summary>結果表（名前欄）に出ているファイル名だけを取り出す。</summary>
    private static string[] ResultNames(string html) =>
        [.. Regex.Matches(html, @"<td data-col=""name"" class=""name"">.*?</span>\s*(?<name>[^\s<][^<]*?)\s*</td>", RegexOptions.Singleline)
            .Select(m => m.Groups["name"].Value.Trim()).Order()];

    // ---- 入力欄 ----

    [Fact]
    public async Task SearchBar_OffersTheSizeCondition_WithEveryComparisonAndUnit()
    {
        var admin = await AdminWithFilesAsync();

        var html = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("<option value=\"Size\">サイズ</option>", html);
        Assert.Contains("name=\"Conditions[0].SizeComparison\"", html);
        foreach (var label in new[] { "≥ 以上", "&gt; より大きい", "≤ 以下", "&lt; より小さい", "= 等しい", "≠ 等しくない" })
        {
            Assert.Contains(label, html);
        }

        Assert.Matches("name=\"Conditions\\[0\\]\\.SizeValue\"[^>]*type=\"number\"|type=\"number\" name=\"Conditions\\[0\\]\\.SizeValue\"", html);
        Assert.Contains("min=\"0\"", html);
        Assert.Contains("step=\"any\"", html);
        foreach (var unit in new[] { "B", "KB", "MB", "GB", "TB" })
        {
            Assert.Contains($"<option value=\"{unit}\"", html);
        }

        // 既定は「以上」「MB」。
        Assert.Contains("<option value=\"GreaterOrEqual\" selected=\"selected\">", html);
        Assert.Contains("<option value=\"MB\" selected=\"selected\">", html);
    }

    [Fact]
    public async Task AddedConditionRows_AlsoHaveTheSizeControls()
    {
        var admin = await AdminWithFilesAsync();

        var html = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);
        var template = html[html.IndexOf("<template id=\"condition-template\">", StringComparison.Ordinal)..];

        Assert.Contains("Conditions[__INDEX__].SizeComparison", template);
        Assert.Contains("Conditions[__INDEX__].SizeValue", template);
        Assert.Contains("Conditions[__INDEX__].SizeUnit", template);
    }

    [Fact]
    public async Task Script_SwitchesTheRowBetweenKeywordDatesAndSize()
    {
        var admin = await AdminWithFilesAsync();

        var script = await admin.GetStringAsync("/js/site.js", TestContext.Current.CancellationToken);

        Assert.Contains("[data-size]", script);
        Assert.Contains("field === 'Size'", script);
    }

    // ---- 検索 ----

    [Theory]
    [InlineData("GreaterThan", "1", "KB", new[] { "big.dat", "huge.dat" })]
    [InlineData("GreaterOrEqual", "2", "KB", new[] { "big.dat", "huge.dat" })]
    [InlineData("GreaterThan", "2", "KB", new[] { "huge.dat" })]
    [InlineData("LessThan", "1", "KB", new[] { "small.dat" })]
    [InlineData("LessOrEqual", "500", "B", new[] { "small.dat" })]
    [InlineData("Equal", "2", "KB", new[] { "big.dat" })]
    [InlineData("Equal", "10", "MB", new[] { "huge.dat" })]
    [InlineData("NotEqual", "2", "KB", new[] { "huge.dat", "small.dat" })]
    [InlineData("GreaterThan", "5", "GB", new string[0])]
    public async Task SearchingBySize_ReturnsTheMatchingFiles_AndNeverTheFolder(
        string comparison, string value, string unit, string[] expected)
    {
        var admin = await AdminWithFilesAsync();

        var html = await admin.GetStringAsync(Query(comparison, value, unit), TestContext.Current.CancellationToken);

        Assert.Equal(expected, ResultNames(html));
        Assert.DoesNotContain("some-folder", html.Split("<tbody>").Last());
    }

    [Fact]
    public async Task FractionalSizes_AreAccepted()
    {
        var admin = await AdminWithFilesAsync();

        var html = await admin.GetStringAsync(Query("Equal", "1.953125", "KB"), TestContext.Current.CancellationToken);   // 2000 バイト

        Assert.Empty(ResultNames(html));
        Assert.Equal(["big.dat"], ResultNames(await admin.GetStringAsync(Query("Equal", "2", "KB"), TestContext.Current.CancellationToken)));
        Assert.Equal(["big.dat", "huge.dat"], ResultNames(await admin.GetStringAsync(Query("GreaterThan", "1.5", "KB"), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task TheEnteredSizeCondition_IsRestoredInTheSearchBar()
    {
        var admin = await AdminWithFilesAsync();

        var html = await admin.GetStringAsync(Query("LessOrEqual", "1.5", "MB"), TestContext.Current.CancellationToken);

        Assert.Contains("<option value=\"Size\" selected=\"selected\">サイズ</option>", html);
        Assert.Contains("<option value=\"LessOrEqual\" selected=\"selected\">", html);
        Assert.Contains("<option value=\"MB\" selected=\"selected\">", html);
        Assert.Contains("value=\"1.5\"", html);
    }

    [Fact]
    public async Task SortingAndPaging_KeepTheSizeCondition()
    {
        var admin = await AdminWithFilesAsync();

        var html = await admin.GetStringAsync(Query("GreaterThan", "1", "KB"), TestContext.Current.CancellationToken);

        var sortLink = Regex.Match(html, "href=\"([^\"]*Sort=Name[^\"]*)\"").Groups[1].Value;
        Assert.Contains("SizeComparison=GreaterThan", sortLink);
        Assert.Contains("SizeValue=1", sortLink);
        Assert.Contains("SizeUnit=KB", sortLink);

        var sorted = await admin.GetStringAsync(System.Net.WebUtility.HtmlDecode(sortLink), TestContext.Current.CancellationToken);
        Assert.Equal(["big.dat", "huge.dat"], ResultNames(sorted));
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("-0.1")]
    public async Task NegativeSizes_AreIgnored_WithAWarning(string value)
    {
        var admin = await AdminWithFilesAsync();

        var html = await admin.GetStringAsync(Query("GreaterThan", value, "KB"), TestContext.Current.CancellationToken);

        Assert.Contains("サイズは 0 以上の数値で指定してください", html);
        Assert.Equal(["big.dat", "huge.dat", "small.dat"], ResultNames(html).Where(n => n != "some-folder"));
    }

    [Fact]
    public async Task AnEmptySizeValue_IsJustNoCondition_WithoutAWarning()
    {
        var admin = await AdminWithFilesAsync();

        var html = await admin.GetStringAsync(Query("GreaterThan", "", "KB"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("サイズは 0 以上の数値", html);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Query("GreaterThan", "", "KB"), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1e400")]
    public async Task NonNumericOrAbsurdValues_DoNotBreakThePage(string value)
    {
        var admin = await AdminWithFilesAsync();

        var response = await admin.GetAsync(Query("GreaterThan", value, "KB"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownComparisonOrUnit_DoNotBreakThePage()
    {
        var admin = await AdminWithFilesAsync();

        var response = await admin.GetAsync(Query("Bogus", "1", "PB"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SizeCanBeCombinedWithOtherConditions()
    {
        var admin = await AdminWithFilesAsync();

        var and = await admin.GetStringAsync(
            Query("GreaterThan", "1", "KB") + "&Conditions[1].Field=FileName&Conditions[1].Text=huge&Operator=And", TestContext.Current.CancellationToken);
        var or = await admin.GetStringAsync(
            Query("LessThan", "1", "KB") + "&Conditions[1].Field=FileName&Conditions[1].Text=huge&Operator=Or", TestContext.Current.CancellationToken);

        Assert.Equal(["huge.dat"], ResultNames(and));
        Assert.Equal(["huge.dat", "small.dat"], ResultNames(or));
    }

    // ---- 操作ログ・保存した検索 ----

    [Fact]
    public async Task SizeSearches_AreWrittenToTheOperationLog()
    {
        var admin = await AdminWithFilesAsync();
        await admin.GetAsync(Query("GreaterOrEqual", "1.5", "MB"), TestContext.Current.CancellationToken);

        var logs = await admin.GetStringAsync("/Logs?Operation=Search", TestContext.Current.CancellationToken);

        Assert.Contains("Size&gt;=1.5MB", logs);
    }

    [Fact]
    public async Task SavedSearch_KeepsTheSizeCondition()
    {
        var admin = await AdminWithFilesAsync();

        var created = await admin.PostFormAsync("/SavedSearches/Create", Query("GreaterThan", "1", "KB"),
        [
            new("Name", "大きいファイル"), new("IsPublic", "false"), new("Operator", "And"), new("Sort", "Path"), new("Desc", "false"),
            new("Conditions[0].Field", "Size"), new("Conditions[0].Text", ""), new("Conditions[0].From", ""), new("Conditions[0].To", ""),
            new("Conditions[0].SizeComparison", "GreaterThan"), new("Conditions[0].SizeValue", "1"), new("Conditions[0].SizeUnit", "KB"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var id = Regex.Match(created.Headers.Location!.ToString(), @"Saved=(\d+)").Groups[1].Value;
        Assert.NotEmpty(id);

        var run = await admin.GetAsync($"/SavedSearches/Run?id={id}", TestContext.Current.CancellationToken);
        var location = Uri.UnescapeDataString(run.Headers.Location!.ToString());
        Assert.Contains("Conditions[0].Field=Size", location);
        Assert.Contains("Conditions[0].SizeComparison=GreaterThan", location);
        Assert.Contains("Conditions[0].SizeValue=1", location);
        Assert.Contains("Conditions[0].SizeUnit=KB", location);

        var page = await admin.GetStringAsync(run.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(["big.dat", "huge.dat"], ResultNames(page));
        Assert.Contains("<option value=\"Size\" selected=\"selected\">サイズ</option>", page);
    }

    [Fact]
    public async Task TheSaveDialog_CarriesTheSizeConditionAsHiddenFields()
    {
        var admin = await AdminWithFilesAsync();

        var html = await admin.GetStringAsync(Query("LessThan", "1.5", "GB"), TestContext.Current.CancellationToken);
        var dialog = html[html.IndexOf("<dialog id=\"save-dialog\"", StringComparison.Ordinal)..];

        Assert.Contains("name=\"Conditions[0].SizeComparison\" value=\"LessThan\"", dialog);
        Assert.Contains("name=\"Conditions[0].SizeValue\" value=\"1.5\"", dialog);
        Assert.Contains("name=\"Conditions[0].SizeUnit\" value=\"GB\"", dialog);
    }
}
