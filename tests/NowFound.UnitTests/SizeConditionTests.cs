using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;
using NowFound.Web.Models;
using NowFound.Web.Services;

namespace NowFound.UnitTests;

public class SizeConditionModelTests
{
    private static SearchCondition Size(decimal? value, SizeUnit unit = SizeUnit.B, SizeComparison comparison = SizeComparison.GreaterOrEqual) =>
        new() { Field = SearchField.Size, SizeValue = value, SizeUnit = unit, SizeComparison = comparison };

    [Theory]
    [InlineData("1", SizeUnit.B, 1L)]
    [InlineData("0", SizeUnit.B, 0L)]
    [InlineData("1", SizeUnit.KB, 1024L)]
    [InlineData("1.5", SizeUnit.KB, 1536L)]
    [InlineData("1", SizeUnit.MB, 1_048_576L)]
    [InlineData("2", SizeUnit.GB, 2_147_483_648L)]
    [InlineData("1", SizeUnit.TB, 1_099_511_627_776L)]
    [InlineData("0.5", SizeUnit.B, 1L)]                  // 四捨五入（0.5 は切り上げ）
    [InlineData("0.4", SizeUnit.B, 0L)]
    [InlineData("0.001", SizeUnit.MB, 1049L)]            // 1048.576 → 1049
    [InlineData("8388607", SizeUnit.TB, 9_223_372_036_854_775_808L - 1_099_511_627_776L)]   // long に収まる最大に近い値
    public void SizeBytes_ConvertsUnits_With1024Multiples(string value, SizeUnit unit, long expectedBytes)
    {
        Assert.Equal(expectedBytes, Size(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), unit).SizeBytes);
    }

    [Theory]
    [InlineData("8388608", SizeUnit.TB)]                 // 2^63 バイト: long に収まらない
    [InlineData("79228162514264337593543950335", SizeUnit.TB)]   // decimal の最大値でも例外にならない
    [InlineData("-1", SizeUnit.B)]
    [InlineData("-0.1", SizeUnit.MB)]
    public void SizeBytes_IsNull_ForNegativeOrTooLargeValues(string value, SizeUnit unit)
    {
        Assert.Null(Size(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), unit).SizeBytes);
    }

    [Fact]
    public void SizeBytes_IsNull_WhenNoValueWasEntered()
    {
        Assert.Null(Size(null).SizeBytes);
    }

    [Fact]
    public void DefaultUnitIsMegabytes_AndDefaultComparisonIsGreaterOrEqual()
    {
        var condition = new SearchCondition { Field = SearchField.Size };

        Assert.Equal(SizeUnit.MB, condition.SizeUnit);
        Assert.Equal(SizeComparison.GreaterOrEqual, condition.SizeComparison);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("0", false)]       // 0 バイトは有効な条件
    [InlineData("10", false)]
    [InlineData("-5", true)]
    public void SizeCondition_IsEmpty_OnlyWhenTheValueIsUnusable(string? value, bool expectedEmpty)
    {
        decimal? parsed = value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expectedEmpty, Size(parsed).IsEmpty);
    }

    [Fact]
    public void HasInvalidSize_IsTrueOnlyForAnEnteredButUnusableValue()
    {
        Assert.True(Size(-1).HasInvalidSize);
        Assert.False(Size(null).HasInvalidSize);       // 未入力は「無効」ではなく、単に条件なし
        Assert.False(Size(5).HasInvalidSize);
        Assert.False(new SearchCondition { Field = SearchField.FileName, Text = "x", SizeValue = -1 }.HasInvalidSize);
    }

    [Fact]
    public void SizeFields_AreIgnoredForOtherFields()
    {
        var condition = new SearchCondition { Field = SearchField.FileName, Text = "abc", SizeValue = 5 };

        Assert.False(condition.IsEmpty);
        Assert.True(new SearchCondition { Field = SearchField.FileName, Text = " ", SizeValue = 5 }.IsEmpty);
    }

    // ---- 保存した検索 ----

    [Fact]
    public void SavedQueryJson_RoundTripsSizeConditions_AndOmitsSizeFieldsForOthers()
    {
        var query = new SavedQuery(
            SearchOperator.And,
            [
                Size(1.5m, SizeUnit.MB, SizeComparison.LessThan),
                new SearchCondition { Field = SearchField.FileName, Text = "abc" },
            ],
            null, null, SearchSortKey.Size, true);

        var json = SavedQueryJson.Serialize(query);
        var loaded = SavedQueryJson.TryDeserialize(json)!;

        Assert.Equal(SearchField.Size, loaded.Conditions[0].Field);
        Assert.Equal(SizeComparison.LessThan, loaded.Conditions[0].SizeComparison);
        Assert.Equal(1.5m, loaded.Conditions[0].SizeValue);
        Assert.Equal(SizeUnit.MB, loaded.Conditions[0].SizeUnit);
        Assert.Equal(1_572_864L, loaded.Conditions[0].SizeBytes);
        Assert.Contains("\"SizeComparison\":\"LessThan\"", json);
        // サイズ以外の条件には、サイズの項目を書かない。
        Assert.DoesNotContain("SizeValue\":null", json);
        Assert.Equal(1, json.Split("SizeValue").Length - 1);
    }

    [Fact]
    public void SavedQueryJson_ReadsDataSavedBeforeSizeExisted()
    {
        const string old = "{\"Version\":1,\"Operator\":\"And\",\"Conditions\":[{\"Field\":\"FileName\",\"Text\":\"abc\"}],\"Sort\":\"Path\",\"Descending\":false}";

        var loaded = SavedQueryJson.TryDeserialize(old)!;

        Assert.Equal("abc", loaded.Conditions.Single().Text);
        Assert.Null(loaded.Conditions.Single().SizeValue);
    }

    [Fact]
    public void SavedQueryRules_KeepsSizeConditions_AndDropsUnusableOnes()
    {
        var normalized = SavedQueryRules.Normalize(new SavedQuery(
            SearchOperator.And, [Size(10, SizeUnit.KB), Size(-3), Size(null)], null, null, SearchSortKey.Path, false));

        var kept = Assert.Single(normalized.Conditions);
        Assert.Equal(10m, kept.SizeValue);
        Assert.Equal(SizeUnit.KB, kept.SizeUnit);
        Assert.Null(SavedQueryRules.Validate(normalized));
    }

    [Fact]
    public void SavedQueryRules_RejectsUndefinedEnumValues()
    {
        var bad = new SearchCondition { Field = SearchField.Size, SizeValue = 1, SizeComparison = (SizeComparison)99 };
        var badUnit = new SearchCondition { Field = SearchField.Size, SizeValue = 1, SizeUnit = (SizeUnit)99 };

        foreach (var condition in new[] { bad, badUnit })
        {
            var query = SavedQueryRules.Normalize(new SavedQuery(SearchOperator.And, [condition], null, null, SearchSortKey.Path, false));
            Assert.Equal("Error.SavedSearch.InvalidCondition", SavedQueryRules.Validate(query)?.Key);
        }
    }

    // ---- 画面側 ----

    [Fact]
    public void RouteValues_CarryTheSizeConditionInAnInvariantFormat()
    {
        var model = new SearchViewModel { Conditions = [Size(1.5m, SizeUnit.MB, SizeComparison.NotEqual)] };

        var values = model.ToRouteValues(1);

        Assert.Equal("Size", values["Conditions[0].Field"]);
        Assert.Equal("NotEqual", values["Conditions[0].SizeComparison"]);
        Assert.Equal("1.5", values["Conditions[0].SizeValue"]);
        Assert.Equal("MB", values["Conditions[0].SizeUnit"]);
    }

    [Fact]
    public void RouteValues_DoNotEmitSizeFieldsForOtherConditions()
    {
        var model = new SearchViewModel { Conditions = [new SearchCondition { Field = SearchField.FileName, Text = "x" }] };

        Assert.DoesNotContain(model.ToRouteValues(1).Keys, k => k.Contains("Size"));
    }

    [Theory]
    [InlineData(SizeComparison.GreaterOrEqual, "Size>=1.5MB")]
    [InlineData(SizeComparison.GreaterThan, "Size>1.5MB")]
    [InlineData(SizeComparison.LessOrEqual, "Size<=1.5MB")]
    [InlineData(SizeComparison.LessThan, "Size<1.5MB")]
    [InlineData(SizeComparison.Equal, "Size=1.5MB")]
    [InlineData(SizeComparison.NotEqual, "Size!=1.5MB")]
    public void OperationLog_DescribesTheSizeCondition(SizeComparison comparison, string expected)
    {
        var text = SearchLocalizerTestHelper.Japanese(localizer =>
            SearchDescriber.Describe([Size(1.5m, SizeUnit.MB, comparison)], SearchOperator.And, null, null, localizer));

        Assert.Equal(expected, text);
    }

    [Fact]
    public void OperationLog_DescribesSizeNextToOtherConditions()
    {
        var text = SearchLocalizerTestHelper.Japanese(localizer => SearchDescriber.Describe(
            [new SearchCondition { Field = SearchField.Extension, Text = "pdf" }, Size(10, SizeUnit.KB, SizeComparison.LessThan)],
            SearchOperator.Or, null, null, localizer));

        Assert.Equal("Extension='pdf' OR Size<10KB", text);
    }

    [Fact]
    public void ConditionRowLabels_CoverEverySizeOption()
    {
        Assert.Equal(Enum.GetValues<SizeComparison>().Order(), SearchLabels.SizeComparisonKeys.Keys.Order());
        Assert.Contains(SearchField.Size, SearchLabels.FieldKeys.Keys);
        foreach (var comparison in Enum.GetValues<SizeComparison>())
        {
            Assert.False(string.IsNullOrWhiteSpace(SearchLabels.Symbol(comparison)));
        }
    }
}

/// <summary>サイズ条件の検索そのもの（SQL への変換）。</summary>
public sealed class SizeConditionSearchTests : IAsyncLifetime
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mk-size"));

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
        await settings.SaveFolderAsync(new FolderEdit { Name = "Size", Path = Root });
        _folderId = (await settings.ListFoldersAsync()).Single().Id;

        var seeder = new FilesSeeder(sp.GetRequiredService<FilesDbContext>());
        await seeder.AddAsync(_folderId, Root, "zero.bin", "bin", 0);
        await seeder.AddAsync(_folderId, Root, "tiny.bin", "bin", 1);
        await seeder.AddAsync(_folderId, Root, "small.bin", "bin", 50);
        await seeder.AddAsync(_folderId, Root, "big.bin", "bin", 100);
        await seeder.AddAsync(_folderId, Root, "kb15.bin", "bin", 1536);                  // 1.5 KB
        await seeder.AddAsync(_folderId, Root, "mb1.bin", "bin", 1_048_576);              // 1 MB
        await seeder.AddAsync(_folderId, Root, "gb3.bin", "bin", 3_221_225_472L);         // 3 GB
        await seeder.AddAsync(_folderId, Root, "folder-a", isDirectory: true);            // サイズなし
        await seeder.AddAsync(_folderId, Root, "folder-b", isDirectory: true);
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    private static SearchCondition Size(SizeComparison comparison, decimal? value, SizeUnit unit = SizeUnit.B) =>
        new() { Field = SearchField.Size, SizeComparison = comparison, SizeValue = value, SizeUnit = unit };

    private async Task<List<string>> NamesAsync(SearchOperator op, params SearchCondition[] conditions)
    {
        var page = await _search.SearchAsync(new SearchRequest(_adminId, true, conditions, op, _folderId, null, 1));
        return page.Items.Select(i => i.Name).Order().ToList();
    }

    [Theory]
    [InlineData(SizeComparison.GreaterOrEqual, new[] { "big.bin", "gb3.bin", "kb15.bin", "mb1.bin", "small.bin" })]
    [InlineData(SizeComparison.GreaterThan, new[] { "big.bin", "gb3.bin", "kb15.bin", "mb1.bin" })]
    [InlineData(SizeComparison.LessOrEqual, new[] { "small.bin", "tiny.bin", "zero.bin" })]
    [InlineData(SizeComparison.LessThan, new[] { "tiny.bin", "zero.bin" })]
    [InlineData(SizeComparison.Equal, new[] { "small.bin" })]
    [InlineData(SizeComparison.NotEqual, new[] { "big.bin", "gb3.bin", "kb15.bin", "mb1.bin", "tiny.bin", "zero.bin" })]
    public async Task EveryComparison_AgainstFiftyBytes_MatchesTheExpectedFiles_AndNeverFolders(
        SizeComparison comparison, string[] expected)
    {
        var names = await NamesAsync(SearchOperator.And, Size(comparison, 50));

        Assert.Equal(expected, names);
        Assert.DoesNotContain(names, n => n.StartsWith("folder-"));
    }

    [Fact]
    public async Task NotEqual_DoesNotReturnFolders_EvenThoughTheirSizeIsNull()
    {
        var names = await NamesAsync(SearchOperator.And, Size(SizeComparison.NotEqual, 12345));

        Assert.DoesNotContain("folder-a", names);
        Assert.Equal(7, names.Count);
    }

    [Fact]
    public async Task ZeroBytes_IsAValidCondition()
    {
        Assert.Equal(["zero.bin"], await NamesAsync(SearchOperator.And, Size(SizeComparison.Equal, 0)));
        Assert.Equal(7, (await NamesAsync(SearchOperator.And, Size(SizeComparison.GreaterOrEqual, 0))).Count);
        Assert.Empty(await NamesAsync(SearchOperator.And, Size(SizeComparison.LessThan, 0)));
    }

    [Theory]
    [InlineData(SizeComparison.Equal, "1.5", SizeUnit.KB, new[] { "kb15.bin" })]
    [InlineData(SizeComparison.Equal, "1536", SizeUnit.B, new[] { "kb15.bin" })]
    [InlineData(SizeComparison.Equal, "1", SizeUnit.MB, new[] { "mb1.bin" })]
    [InlineData(SizeComparison.GreaterThan, "1", SizeUnit.GB, new[] { "gb3.bin" })]
    [InlineData(SizeComparison.GreaterOrEqual, "3", SizeUnit.GB, new[] { "gb3.bin" })]
    [InlineData(SizeComparison.GreaterThan, "3", SizeUnit.GB, new string[0])]
    [InlineData(SizeComparison.LessThan, "1", SizeUnit.KB, new[] { "big.bin", "small.bin", "tiny.bin", "zero.bin" })]
    [InlineData(SizeComparison.GreaterOrEqual, "1", SizeUnit.KB, new[] { "gb3.bin", "kb15.bin", "mb1.bin" })]
    [InlineData(SizeComparison.LessOrEqual, "0.001", SizeUnit.MB, new[] { "big.bin", "small.bin", "tiny.bin", "zero.bin" })]   // 0.001 MB = 1,049 バイト（四捨五入）
    public async Task Units_AreConvertedWith1024Multiples(SizeComparison comparison, string value, SizeUnit unit, string[] expected)
    {
        var names = await NamesAsync(
            SearchOperator.And,
            Size(comparison, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), unit));

        Assert.Equal(expected, names);
    }

    [Fact]
    public async Task SizeRange_UsingTwoConditions_WithAnd()
    {
        var names = await NamesAsync(
            SearchOperator.And,
            Size(SizeComparison.GreaterOrEqual, 1, SizeUnit.KB), Size(SizeComparison.LessThan, 2, SizeUnit.MB));

        Assert.Equal(["kb15.bin", "mb1.bin"], names);
    }

    [Fact]
    public async Task SizeWithOr_CombinesWithOtherConditions()
    {
        var names = await NamesAsync(
            SearchOperator.Or,
            Size(SizeComparison.LessThan, 1),
            new SearchCondition { Field = SearchField.FileName, Text = "gb3" });

        Assert.Equal(["gb3.bin", "zero.bin"], names);
    }

    [Fact]
    public async Task SizeWithAnd_NarrowsAnotherCondition()
    {
        var names = await NamesAsync(
            SearchOperator.And,
            new SearchCondition { Field = SearchField.FileName, Text = "bin" },
            Size(SizeComparison.GreaterThan, 100));

        Assert.Equal(["gb3.bin", "kb15.bin", "mb1.bin"], names);
    }

    [Fact]
    public async Task UnusableSizeConditions_AreIgnored_NotTreatedAsNoMatch()
    {
        var all = await NamesAsync(SearchOperator.And);

        Assert.Equal(all, await NamesAsync(SearchOperator.And, Size(SizeComparison.GreaterThan, null)));
        Assert.Equal(all, await NamesAsync(SearchOperator.And, Size(SizeComparison.GreaterThan, -5)));
        Assert.Equal(all, await NamesAsync(SearchOperator.And, Size(SizeComparison.GreaterThan, 99999999999m, SizeUnit.TB)));
    }

    [Fact]
    public async Task SortingBySize_StillWorksTogetherWithASizeCondition()
    {
        var page = await _search.SearchAsync(new SearchRequest(
            _adminId, true, [Size(SizeComparison.GreaterOrEqual, 50)], SearchOperator.And, _folderId, null, 1, SearchSortKey.Size, true), TestContext.Current.CancellationToken);

        Assert.Equal(["gb3.bin", "mb1.bin", "kb15.bin", "big.bin", "small.bin"], page.Items.Select(i => i.Name));
    }
}
