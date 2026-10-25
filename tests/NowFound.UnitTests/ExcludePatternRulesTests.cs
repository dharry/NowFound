using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Indexing;
using NowFound.Application.Settings;
using NowFound.Infrastructure.Indexing;

namespace NowFound.UnitTests;

public class ExcludePatternRulesTests
{
    [Fact]
    public void BuiltIn_IsValid_AndFitsInOneSetting()
    {
        var (patterns, errors) = ExcludePatternRules.Normalize(ExcludePatternRules.BuiltIn);
        Assert.Empty(errors);
        Assert.Equal(ExcludePatternRules.BuiltIn.Count, patterns.Count);
        Assert.Contains("#*#", patterns);
        Assert.Contains("__MACOSX/", patterns);
    }

    [Fact]
    public void Normalize_TrimsAndDropsBlankAndDuplicates()
    {
        var (patterns, errors) = ExcludePatternRules.Normalize(["  *.tmp ", "", "*.TMP", "node_modules"]);
        Assert.Empty(errors);
        Assert.Equal(["*.tmp", "node_modules"], patterns);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a//")]
    [InlineData("tab\tname")]
    public void Normalize_RejectsInvalidShapes(string pattern) =>
        Assert.Contains(ExcludePatternRules.Normalize([pattern]).Errors,
            e => e.Key is "Error.ExcludePattern.InvalidShape" or "Error.ExcludePattern.ControlChar");

    [Fact]
    public void Normalize_RejectsTooLongOrTooMany()
    {
        var tooLong = ExcludePatternRules.Normalize([new string('a', ExcludePatternRules.MaxPatternLength + 1)]).Errors.Single();
        Assert.Equal("Error.ExcludePattern.TooLong", tooLong.Key);
        Assert.Equal(ExcludePatternRules.MaxPatternLength, tooLong.Args[0]);

        var tooMany = ExcludePatternRules.Normalize(Enumerable.Range(0, ExcludePatternRules.MaxPatterns + 1).Select(i => $"p{i}")).Errors;
        Assert.Contains(tooMany, e => e.Key == "Error.ExcludePattern.TooMany" && e.Args[0] is ExcludePatternRules.MaxPatterns);

        var totalTooLong = ExcludePatternRules.Normalize(Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 90))).Errors;
        Assert.Contains(totalTooLong, e => e.Key == "Error.ExcludePattern.TotalTooLong");
    }

    [Fact]
    public void Split_HandlesBothNewlineStyles() =>
        Assert.Equal(["a", "b", "c"], ExcludePatternRules.Split("a\r\nb\nc\r\n"));
}

public sealed class DefaultExcludePatternsServiceTests
{
    [Fact]
    public async Task Default_IsBuiltIn_UntilCustomized_AndCanBeResetOrEmptied()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAdminSettingsService>();
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await service.IsDefaultExcludePatternsCustomizedAsync(ct));
        Assert.Equal(ExcludePatternRules.BuiltIn, await service.GetDefaultExcludePatternsAsync(ct));

        Assert.True((await service.SetDefaultExcludePatternsAsync(["*.bak", " *.bak", "tmp/"], ct)).Succeeded);
        Assert.True(await service.IsDefaultExcludePatternsCustomizedAsync(ct));
        Assert.Equal(["*.bak", "tmp/"], await service.GetDefaultExcludePatternsAsync(ct));

        // 空にした場合は「何も除外しない」。組み込みの既定には戻らない。
        Assert.True((await service.SetDefaultExcludePatternsAsync([], ct)).Succeeded);
        Assert.Empty(await service.GetDefaultExcludePatternsAsync(ct));
        Assert.True(await service.IsDefaultExcludePatternsCustomizedAsync(ct));

        await service.ResetDefaultExcludePatternsAsync(ct);
        Assert.False(await service.IsDefaultExcludePatternsCustomizedAsync(ct));
        Assert.Equal(ExcludePatternRules.BuiltIn, await service.GetDefaultExcludePatternsAsync(ct));
    }

    [Fact]
    public async Task InvalidPatterns_AreRejected_AndKeepThePreviousValue()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAdminSettingsService>();
        var ct = TestContext.Current.CancellationToken;

        await service.SetDefaultExcludePatternsAsync(["*.bak"], ct);
        var result = await service.SetDefaultExcludePatternsAsync(["ok", "a/b"], ct);

        Assert.False(result.Succeeded);
        Assert.Equal(["*.bak"], await service.GetDefaultExcludePatternsAsync(ct));
    }
}

public sealed class ScannerDirectoryOnlyExcludeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nowfound-excl-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Scan_WithBuiltInDefaults_SkipsJunkAndWholeJunkFolders_ButKeepsSameNamedFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "__MACOSX"));
        File.WriteAllText(Path.Combine(_root, "__MACOSX", "inside.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "readme.md"), "x");
        File.WriteAllText(Path.Combine(_root, "docs", "Thumbs.db"), "x");
        File.WriteAllText(Path.Combine(_root, "docs", "~$readme.md"), "x");
        File.WriteAllText(Path.Combine(_root, "docs", "readme.md.swp"), "x");
        File.WriteAllText(Path.Combine(_root, "docs", "__MACOSX"), "a file, not a folder");

        var entries = new FileScanner()
            .Scan(new IndexTarget(1, _root, false, ExcludePatternRules.BuiltIn))
            .Select(e => (e.Name, e.IsDirectory))
            .Order()
            .ToList();

        // ルートの __MACOSX フォルダは中身ごと除外。docs 内の同名「ファイル」は残る。
        Assert.Equal(
            [("__MACOSX", false), ("docs", true), ("readme.md", false)],
            entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList());
    }
}
