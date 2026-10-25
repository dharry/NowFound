using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NowFound.Application.FileKinds;

namespace NowFound.UnitTests;

/// <summary>サービス・ルールが返すエラーのキーと、ファイルの種類の翻訳キーが、Messages リソースに存在すること。</summary>
public partial class ServiceMessageKeyTests
{
    private static string SourceRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src"));

    [GeneratedRegex("""
        "(Error\.[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*)"
        """)]
    private static partial Regex ErrorKeyLiteral();

    private static HashSet<string> LoadKeys(string fileName) =>
        [.. XDocument.Load(Path.Combine(SourceRoot(), "NowFound.Web", "Resources", fileName)).Root!.Elements("data")
            .Select(d => (string)d.Attribute("name")!)];

    /// <summary>ソースに直接書かれた Error.* のキー。ExtensionKey は組み立てて返すため、別に列挙する。</summary>
    private static IEnumerable<string> KeysInSource()
    {
        var layers = new[] { "NowFound.Application", "NowFound.Infrastructure", "NowFound.Domain" };
        var files = layers
            .SelectMany(layer => Directory.EnumerateFiles(Path.Combine(SourceRoot(), layer), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains("Migrations"));

        return files.SelectMany(f => ErrorKeyLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value));
    }

    private static readonly string[] ExtensionKeyNames = ["InvalidChar", "Dotted", "TooLong", "TooMany"];

    private static IEnumerable<string> AllKeys() =>
        KeysInSource()
            .Concat(ExtensionKeyNames.SelectMany(n => new[] { $"Error.FileKind.Extension.{n}.Named", $"Error.FileKind.Extension.{n}.Row" }))
            .Concat(DefaultFileKinds.Definitions.Select(d => d.LabelKey!))
            .Distinct()
            .Order();

    [Fact]
    public void TheSourceScan_FindsTheKeys()
    {
        // 正規表現やパスが壊れて、検査が空振りにならないための確認。
        var keys = KeysInSource().ToList();
        Assert.Contains("Error.Folder.PathDuplicate", keys);
        Assert.Contains("Error.FileKind.Label.Required", keys);
        Assert.Contains("Error.SavedSearch.NotFound", keys);
        Assert.Contains("Error.ExcludePattern.TooMany", keys);
    }

    [Theory]
    [InlineData("MessagesResource.resx")]
    [InlineData("MessagesResource.en.resx")]
    public void EveryKeyReturnedByTheServices_ExistsInTheMessagesResource(string fileName)
    {
        var defined = LoadKeys(fileName);

        Assert.All(AllKeys(), key => Assert.True(defined.Contains(key), $"{fileName} にキー {key} がありません"));
    }
}
