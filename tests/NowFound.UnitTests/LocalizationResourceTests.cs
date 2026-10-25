using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NowFound.Application;
using NowFound.Domain;

namespace NowFound.UnitTests;

public partial class LocalizationResourceTests
{
    private static string ResourcesDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src", "NowFound.Web", "Resources"));

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderRegex();

    private static Dictionary<string, string> Load(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    private static string[] Areas() =>
        [.. Directory.GetFiles(ResourcesDirectory(), "*Resource.resx").Select(p => Path.GetFileName(p)[..^"Resource.resx".Length]).Order()];

    public static TheoryData<string> AreaNames() => [.. Areas()];

    [Theory]
    [MemberData(nameof(AreaNames))]
    public void EveryKey_ExistsInBothLanguages_WithNonEmptyText(string area)
    {
        var ja = Load(Path.Combine(ResourcesDirectory(), $"{area}Resource.resx"));
        var en = Load(Path.Combine(ResourcesDirectory(), $"{area}Resource.en.resx"));

        Assert.Empty(ja.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(ja.Keys));
        Assert.All(ja, p => Assert.False(string.IsNullOrWhiteSpace(p.Value), $"{area}.ja: {p.Key} が空です"));
        Assert.All(en, p => Assert.False(string.IsNullOrWhiteSpace(p.Value), $"{area}.en: {p.Key} が空です"));
    }

    [Theory]
    [MemberData(nameof(AreaNames))]
    public void Placeholders_MatchBetweenLanguages(string area)
    {
        var ja = Load(Path.Combine(ResourcesDirectory(), $"{area}Resource.resx"));
        var en = Load(Path.Combine(ResourcesDirectory(), $"{area}Resource.en.resx"));

        foreach (var (key, text) in ja.Where(p => en.ContainsKey(p.Key)))
        {
            var jaPlaceholders = PlaceholderRegex().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order();
            var enPlaceholders = PlaceholderRegex().Matches(en[key]).Select(m => m.Groups[1].Value).Distinct().Order();
            Assert.True(jaPlaceholders.SequenceEqual(enPlaceholders), $"{area}.{key}: {{n}} の使い方が日本語と英語で違います");
        }
    }

    [Fact]
    public void EnglishText_DoesNotContainJapanese()
    {
        var japanese = new Regex("[぀-ヿ一-鿿]");
        foreach (var area in Areas())
        {
            var en = Load(Path.Combine(ResourcesDirectory(), $"{area}Resource.en.resx"));
            Assert.All(en, p => Assert.False(japanese.IsMatch(p.Value), $"{area}.en: {p.Key} に日本語が残っています"));
        }
    }

    [Fact]
    public void AppLanguages_HasJapaneseAndEnglish_AndNormalizesUnknownValues()
    {
        Assert.Equal(["ja", "en"], AppLanguages.All.Select(l => l.Id));
        Assert.Equal("en", AppLanguages.Normalize("en"));
        Assert.Equal("ja", AppLanguages.Normalize(null));
        Assert.Equal("ja", AppLanguages.Normalize("EN"));
        Assert.Equal("ja", AppLanguages.Normalize("en\"><script>"));
    }

    [Fact]
    public void LocalizedMessage_FormatsArgsInvariantly_AndConvertsFromKey()
    {
        LocalizedMessage fromKey = "Some.Key";
        var message = new LocalizedMessage("Log.X", "a", 1234.5m, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), null);

        Assert.Equal("Some.Key", fromKey.Key);
        Assert.Empty(fromKey.Args);
        Assert.Equal(["a", "1234.5", "01/02/2026 03:04:05", ""], message.FormatArgs());
        Assert.Equal("text", LocalizedMessage.Raw("text").Args[0]);
    }
}
