using NowFound.Application;
using NowFound.Application.FileKinds;

namespace NowFound.UnitTests;

public class FileKindRulesTests
{
    private static FileKindEdit Folder(string icon = "📁", string label = "フォルダ") =>
        new() { Key = "folder", Icon = icon, Label = label };

    private static FileKindEdit Generic(string icon = "📃", string label = "ファイル") =>
        new() { Key = "file", Icon = icon, Label = label };

    private static FileKindEdit Kind(string label, string icon, string? extensions, string? key = null) =>
        new() { Key = key, Label = label, Icon = icon, Extensions = extensions };

    private static FileKindValidation Validate(params FileKindEdit[] custom) =>
        FileKindRules.Validate([Folder(), Generic(), .. custom]);

    private static bool Has(IEnumerable<LocalizedMessage> errors, string key) => errors.Any(e => e.Key == key);

    // ---- 正規化 ----

    [Theory]
    [InlineData("TXT", "txt")]
    [InlineData(".Md", "md")]
    [InlineData("  .PDF ", "pdf")]
    [InlineData("..jpg", "jpg")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeExtension_LowercasesAndStripsDotsAndSpaces(string? input, string expected)
    {
        Assert.Equal(expected, FileKindRules.NormalizeExtension(input));
    }

    [Fact]
    public void ParseExtensions_AcceptsEverySeparator_AndRemovesDuplicates()
    {
        var errors = new List<LocalizedMessage>();

        var result = FileKindRules.ParseExtensions("txt, .LOG;conf、cfg，ini\nyml\r\n  txt\tlog", "テスト", 1, errors);

        Assert.Equal(["txt", "log", "conf", "cfg", "ini", "yml"], result);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n , ;")]
    public void ParseExtensions_EmptyInput_IsAnEmptyList(string? raw)
    {
        var errors = new List<LocalizedMessage>();

        Assert.Empty(FileKindRules.ParseExtensions(raw, "テスト", 1, errors));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("tar.gz", "Error.FileKind.Extension.Dotted.Named")]
    [InlineData("*", "Error.FileKind.Extension.InvalidChar.Named")]
    [InlineData("a*b", "Error.FileKind.Extension.InvalidChar.Named")]
    [InlineData("*.tar.gz", "Error.FileKind.Extension.Dotted.Named")]
    [InlineData("a/b", "Error.FileKind.Extension.InvalidChar.Named")]
    [InlineData("a\\b", "Error.FileKind.Extension.InvalidChar.Named")]
    [InlineData("a?b", "Error.FileKind.Extension.InvalidChar.Named")]
    [InlineData("a\"b", "Error.FileKind.Extension.InvalidChar.Named")]
    [InlineData("a<b", "Error.FileKind.Extension.InvalidChar.Named")]
    [InlineData("this-extension-is-way-too-long", "Error.FileKind.Extension.TooLong.Named")]
    public void ParseExtensions_RejectsUnusableExtensions(string raw, string expectedMessage)
    {
        var errors = new List<LocalizedMessage>();

        var result = FileKindRules.ParseExtensions(raw, "テスト", 1, errors);

        Assert.Empty(result);
        Assert.Contains(errors, e => e.Key == expectedMessage && e.Args[0] is "テスト");
    }

    [Theory]
    [InlineData("*.txt", "txt")]
    [InlineData("*.TXT", "txt")]
    [InlineData("  *.log ", "log")]
    public void ParseExtensions_AcceptsALeadingWildcardDot(string raw, string expected)
    {
        var errors = new List<LocalizedMessage>();

        Assert.Equal([expected], FileKindRules.ParseExtensions(raw, "テスト", 1, errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void ParseExtensions_DottedExtension_SuggestsTheLastPart()
    {
        var errors = new List<LocalizedMessage>();

        FileKindRules.ParseExtensions("tar.gz", "アーカイブ", 1, errors);

        var error = Assert.Single(errors);
        Assert.Equal("Error.FileKind.Extension.Dotted.Named", error.Key);
        Assert.Equal(["アーカイブ", "tar.gz", "gz"], error.Args);
    }

    [Fact]
    public void ParseExtensions_TooMany_IsRejected()
    {
        var errors = new List<LocalizedMessage>();
        var raw = string.Join(',', Enumerable.Range(0, FileKindRules.MaxExtensionsPerKind + 1).Select(i => $"e{i}"));

        FileKindRules.ParseExtensions(raw, "大量", 1, errors);

        var error = Assert.Single(errors);
        Assert.Equal("Error.FileKind.Extension.TooMany.Named", error.Key);
        Assert.Equal(["大量", FileKindRules.MaxExtensionsPerKind], error.Args);
    }

    // ---- 全体の検証 ----

    [Fact]
    public void ValidInput_ProducesDefinitionsInOrder_WithGeneratedKeysForNewRows()
    {
        var result = Validate(Kind("ログ", "📜", "log, trace"), Kind("設定", "🔧", "ini", key: "settings"));

        Assert.True(result.Succeeded);
        var definitions = result.Definitions!;
        Assert.Equal(["folder", "file"], definitions.Take(2).Select(d => d.Key));
        Assert.Matches("^custom-[0-9a-f]{8}$", definitions[2].Key);
        Assert.Equal("settings", definitions[3].Key);
        Assert.Equal(["log", "trace"], definitions[2].Extensions);
        Assert.False(definitions[2].IsSystem);
        Assert.True(definitions[0].IsSystem);
    }

    [Fact]
    public void GeneratedKeys_AreUnique()
    {
        var result = Validate(Kind("A", "a", "a"), Kind("B", "b", "b"), Kind("C", "c", "c"));

        Assert.Equal(5, result.Definitions!.Select(d => d.Key).Distinct().Count());
    }

    [Fact]
    public void SystemRows_IgnoreExtensions()
    {
        var folder = Folder();
        folder.Extensions = "txt, log";

        var result = FileKindRules.Validate([folder, Generic()]);

        Assert.True(result.Succeeded);
        Assert.All(result.Definitions!, d => Assert.Empty(d.Extensions));
    }

    [Fact]
    public void OnlySystemRows_IsValid_MeaningNoExtensionHasASpecialIcon()
    {
        Assert.True(FileKindRules.Validate([Folder("🗂️"), Generic("❓")]).Succeeded);
    }

    [Fact]
    public void SameExtensionInTwoKinds_IsRejected_NamingBothKinds()
    {
        var result = Validate(Kind("ログ", "📜", "log, txt"), Kind("テキスト", "📝", "txt"));

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Error.FileKind.Extension.Duplicate", error.Key);
        Assert.Equal(["txt", "ログ", "テキスト"], error.Args);
    }

    [Fact]
    public void SameExtensionTwiceInOneKind_IsFine()
    {
        Assert.True(Validate(Kind("ログ", "📜", "log, LOG, .log")).Succeeded);
    }

    [Fact]
    public void ExtensionCase_IsIgnoredWhenComparingKinds()
    {
        Assert.False(Validate(Kind("A", "a", "PDF"), Kind("B", "b", "pdf")).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLabel_IsRejected(string label)
    {
        var result = Validate(Kind(label, "x", "a"));

        Assert.Contains(result.Errors, e => e.Key == "Error.FileKind.Label.Required" && e.Args[0] is 3);
    }

    [Fact]
    public void LongLabel_IsRejected_AtTheBoundary()
    {
        Assert.True(Validate(Kind(new string('あ', 50), "x", "a")).Succeeded);
        Assert.Contains(Validate(Kind(new string('あ', 51), "x", "a")).Errors, e => e.Key == "Error.FileKind.Label.TooLong" && e.Args[1] is 50);
    }

    [Fact]
    public void DuplicateLabels_AreRejected_IgnoringCase_IncludingSystemRows()
    {
        Assert.Contains(Validate(Kind("Photo", "a", "jpg"), Kind("photo", "b", "png")).Errors, e => e.Key == "Error.FileKind.Label.Duplicate" && e.Args[0] is "photo");
        Assert.True(Has(Validate(Kind("フォルダ", "a", "x")).Errors, "Error.FileKind.Label.Duplicate"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankIcon_IsRejected(string icon)
    {
        Assert.Contains(Validate(Kind("A", icon, "a")).Errors, e => e.Key == "Error.FileKind.Icon.Required");
    }

    [Theory]
    [InlineData("📝")]                          // 1 つの絵文字
    [InlineData("⚙️")]                          // 異体字セレクタ付き
    [InlineData("👨‍👩‍👧‍👦")]                          // 合成した絵文字（見た目は 1 文字）
    [InlineData("🇯🇵")]                          // 国旗（2 つの記号で 1 文字）
    [InlineData("PDF")]                         // 短い文字
    [InlineData("12345678")]                    // 8 文字は可
    public void Icon_AcceptsEmojiAndShortText(string icon)
    {
        Assert.True(Validate(Kind("A", icon, "a")).Succeeded, icon);
    }

    [Theory]
    [InlineData("123456789")]                   // 9 文字
    [InlineData("😀😀😀😀😀😀😀😀😀")]
    public void Icon_TooLong_IsRejected(string icon)
    {
        Assert.Contains(Validate(Kind("A", icon, "a")).Errors, e => e.Key == "Error.FileKind.Icon.TooLong" && e.Args[1] is 8);
    }

    [Fact]
    public void Icon_WithControlCharacters_IsRejected()
    {
        Assert.Contains(Validate(Kind("A", "a\u0007b", "a")).Errors, e => e.Key == "Error.FileKind.Icon.InvalidChar");
        Assert.Contains(Validate(Kind("A", "a\nb", "a")).Errors, e => e.Key == "Error.FileKind.Icon.InvalidChar");
    }

    [Theory]
    [InlineData("Bad Key")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    [InlineData("a/b")]
    public void InvalidKey_IsRejected(string key)
    {
        Assert.Contains(Validate(Kind("A", "a", "a", key: key)).Errors, e => e.Key == "Error.FileKind.Key.Invalid");
    }

    [Fact]
    public void DuplicateKey_IsRejected()
    {
        var result = Validate(Kind("A", "a", "a", key: "same"), Kind("B", "b", "b", key: "same"));

        Assert.Contains(result.Errors, e => e.Key == "Error.FileKind.Key.Duplicate" && e.Args[1] is "same");
    }

    [Fact]
    public void ARegularRowCannotImpersonateASystemRow()
    {
        // 「folder」「file」の識別子はシステム行の専用。2 行目があると、必須行が「ちょうど 1 行」でなくなりエラーになる。
        var result = Validate(Kind("偽のフォルダ", "x", "zzz", key: "folder"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Key is "Error.FileKind.Required.Folder" or "Error.FileKind.Key.Duplicate");
    }

    [Fact]
    public void MissingSystemRows_AreRejected()
    {
        Assert.Contains(FileKindRules.Validate([Generic()]).Errors, e => e.Key == "Error.FileKind.Required.Folder");
        Assert.Contains(FileKindRules.Validate([Folder()]).Errors, e => e.Key == "Error.FileKind.Required.Generic");
        Assert.Equal(["Error.FileKind.Required.Folder", "Error.FileKind.Required.Generic"], FileKindRules.Validate([]).Errors.Select(e => e.Key));
    }

    [Fact]
    public void TooManyKinds_AreRejected()
    {
        var edits = new List<FileKindEdit> { Folder(), Generic() };
        edits.AddRange(Enumerable.Range(0, FileKindRules.MaxKinds + 1).Select(i => Kind($"K{i}", "x", $"e{i}")));

        Assert.Contains(FileKindRules.Validate(edits).Errors, e => e.Key == "Error.FileKind.TooMany" && e.Args[0] is 100);
    }

    [Fact]
    public void Exactly100Kinds_AreAllowed()
    {
        var edits = new List<FileKindEdit> { Folder(), Generic() };
        edits.AddRange(Enumerable.Range(0, FileKindRules.MaxKinds).Select(i => Kind($"K{i}", "x", $"e{i}")));

        Assert.True(FileKindRules.Validate(edits).Succeeded);
    }

    [Fact]
    public void AllProblemsAreReportedAtOnce_NotJustTheFirst()
    {
        var result = Validate(Kind("", "", "tar.gz"), Kind("B", "b", "*"));

        Assert.False(result.Succeeded);
        Assert.True(result.Errors.Count >= 4, string.Join(" / ", result.Errors.Select(e => e.Key)));
    }

    [Fact]
    public void LabelsAndIcons_AreTrimmed()
    {
        var result = Validate(Kind("  ログ  ", "  📜  ", "log"));

        var definition = result.Definitions![2];
        Assert.Equal("ログ", definition.Label);
        Assert.Equal("📜", definition.Icon);
    }

    [Fact]
    public void TheBuiltInDefaults_PassTheirOwnValidation()
    {
        var edits = DefaultFileKinds.Definitions.Select(d => new FileKindEdit
        {
            Key = d.Key, Label = d.Label, Icon = d.Icon, Extensions = string.Join(", ", d.Extensions),
        }).ToList();

        var result = FileKindRules.Validate(edits);

        Assert.True(result.Succeeded, string.Join(" / ", result.Errors.Select(e => e.Key)));
        Assert.Equal(DefaultFileKinds.Definitions.Select(d => d.Key), result.Definitions!.Select(d => d.Key));
    }

    [Fact]
    public void ParseExtensions_WithoutALabel_ReportsTheRowNumber()
    {
        var errors = new List<LocalizedMessage>();

        FileKindRules.ParseExtensions("a*b, tar.gz, this-extension-is-way-too-long", "", 4, errors);

        Assert.Equal(
            ["Error.FileKind.Extension.InvalidChar.Row", "Error.FileKind.Extension.Dotted.Row", "Error.FileKind.Extension.TooLong.Row"],
            errors.Select(e => e.Key));
        Assert.All(errors, e => Assert.Equal(4, e.Args[0]));
    }

    [Fact]
    public void Validate_RowOwnedExtensionError_UsesTheRowOfTheKind()
    {
        var result = Validate(Kind("", "x", "a/b"));

        Assert.Contains(result.Errors, e => e.Key == "Error.FileKind.Extension.InvalidChar.Row" && e.Args[0] is 3);
    }

    [Fact]
    public void Validate_IdenticalErrors_AreReportedOnce()
    {
        var result = Validate(Kind("A", "x", "a/b, A/B"), Kind("B", "x", "a/b"));

        Assert.Equal(result.Errors.Count, result.Errors.Select(e => (e.Key, string.Join(',', e.FormatArgs()))).Distinct().Count());
    }

    [Fact]
    public void Validate_BuiltInNames_KeepTheirTranslationKey_ButCustomNamesDoNot()
    {
        var result = FileKindRules.Validate(
        [
            Folder(),                                   // 既定の名前のまま
            Generic(label: "その他"),                   // 管理者が付けた名前
            Kind("オフィスファイル", "📄", "docx", key: "office"),
            Kind("事務ファイル", "📄", "xlsx", key: "custom-office"),
            Kind("オフィスファイル2", "📄", "pptx", key: "office-2"),
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal(["FileKind.folder", null, "FileKind.office", null, null], result.Definitions!.Select(d => d.LabelKey));
    }

    [Fact]
    public void LabelKeyFor_RequiresTheBuiltInKeyAndTheUnchangedName()
    {
        Assert.Equal("FileKind.pdf", DefaultFileKinds.LabelKeyFor("pdf", "PDFファイル"));
        Assert.Null(DefaultFileKinds.LabelKeyFor("pdf", "PDF"));
        Assert.Null(DefaultFileKinds.LabelKeyFor("unknown", "PDFファイル"));
    }

    [Fact]
    public void EveryBuiltInKind_HasAMatchingTranslationKey()
    {
        Assert.All(DefaultFileKinds.Definitions, d => Assert.Equal("FileKind." + d.Key, d.LabelKey));
        Assert.All(FileKindCatalog.Default.Kinds.Append(FileKindCatalog.Default.Folder).Append(FileKindCatalog.Default.Generic),
            k => Assert.Equal("FileKind." + k.Id, k.LabelKey));
    }
}
