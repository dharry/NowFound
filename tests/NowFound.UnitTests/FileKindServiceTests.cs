using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.FileKinds;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

public class FileKindServiceTests
{
    private static FileKindEdit Edit(string? key, string label, string icon, string? extensions = null) =>
        new() { Key = key, Label = label, Icon = icon, Extensions = extensions };

    private static List<FileKindEdit> Minimal(params FileKindEdit[] custom) =>
        [Edit("folder", "フォルダ", "📁"), Edit("file", "ファイル", "📃"), .. custom];

    [Fact]
    public async Task WithoutAnySetting_TheBuiltInDefaultsAreUsed()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();

        var catalog = await service.GetCatalogAsync(TestContext.Current.CancellationToken);

        Assert.False(await service.IsCustomizedAsync(TestContext.Current.CancellationToken));
        Assert.Same(DefaultFileKinds.Definitions, await service.GetDefinitionsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text", catalog.For("txt", false).Id);
        Assert.Equal("📁", catalog.Folder.Icon);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().FileKindSettings.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_ReplacesEverything_AndIsUsedByTheCatalog()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();

        var result = await service.SaveAsync(Minimal(Edit(null, "ログ", "📜", "log, trace"), Edit("pdf", "PDF", "PDF", "pdf")), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(await service.IsCustomizedAsync(TestContext.Current.CancellationToken));
        var catalog = await service.GetCatalogAsync(TestContext.Current.CancellationToken);
        Assert.Equal("📜", catalog.For("TRACE", false).Icon);
        Assert.Equal("ログ", catalog.For("log", false).Label);
        Assert.Equal("PDF", catalog.For("pdf", false).Icon);
        // 保存した一覧にない拡張子は、組み込みの既定ではなく「ファイル」になる（管理者が消した扱い）。
        Assert.Same(catalog.Generic, catalog.For("txt", false));
        Assert.Equal("📃", catalog.Generic.Icon);
    }

    [Fact]
    public async Task Save_PreservesOrder_AndExistingKeys_AndGeneratesKeysForNewRows()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();

        await service.SaveAsync(Minimal(Edit("zzz", "最後", "z", "z"), Edit("aaa", "最初", "a", "a"), Edit(null, "新規", "n", "n")), TestContext.Current.CancellationToken);
        var definitions = await service.GetDefinitionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["folder", "file", "zzz", "aaa"], definitions.Take(4).Select(d => d.Key));
        Assert.StartsWith("custom-", definitions[4].Key);
        Assert.Equal(5, definitions.Count);
    }

    [Fact]
    public async Task Save_InvalidInput_ChangesNothing()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();
        await service.SaveAsync(Minimal(Edit("log", "ログ", "📜", "log")), TestContext.Current.CancellationToken);

        var result = await service.SaveAsync(Minimal(Edit("a", "A", "a", "x"), Edit("b", "B", "b", "x")), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("Error.FileKind.Extension.Duplicate", Assert.Single(result.Errors).Key);
        var catalog = await service.GetCatalogAsync(TestContext.Current.CancellationToken);
        Assert.Equal("log", catalog.For("log", false).Id);        // 直前の保存内容が残っている
        Assert.Same(catalog.Generic, catalog.For("x", false));
    }

    [Fact]
    public async Task Save_CanBeRepeated_WithoutDuplicatingRows()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await service.SaveAsync(Minimal(Edit("a", "A", "a", "x")), TestContext.Current.CancellationToken);
        await service.SaveAsync(Minimal(Edit("a", "A2", "a2", "x, y")), TestContext.Current.CancellationToken);

        Assert.Equal(3, await db.FileKindSettings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal("A2", (await service.GetCatalogAsync(TestContext.Current.CancellationToken)).For("y", false).Label);
    }

    [Fact]
    public async Task Reset_RestoresTheBuiltInDefaults()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();
        await service.SaveAsync(Minimal(Edit("log", "ログ", "📜", "log")), TestContext.Current.CancellationToken);

        await service.ResetAsync(TestContext.Current.CancellationToken);

        Assert.False(await service.IsCustomizedAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text", (await service.GetCatalogAsync(TestContext.Current.CancellationToken)).For("txt", false).Id);
        Assert.Equal("📁", (await service.GetCatalogAsync(TestContext.Current.CancellationToken)).Folder.Icon);
    }

    [Fact]
    public async Task Reset_WhenNothingIsCustomized_IsHarmless()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();

        await service.ResetAsync(TestContext.Current.CancellationToken);

        Assert.False(await service.IsCustomizedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SystemRows_CanHaveTheirIconAndLabelChanged()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();

        await service.SaveAsync([Edit("folder", "ディレクトリ", "🗂️"), Edit("file", "その他", "❔")], TestContext.Current.CancellationToken);
        var catalog = await service.GetCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("ディレクトリ", "🗂️"), (catalog.Folder.Label, catalog.Folder.Icon));
        Assert.Equal(("その他", "❔"), (catalog.Generic.Label, catalog.Generic.Icon));
        Assert.Equal(catalog.Folder, catalog.For("txt", true));
        Assert.Empty(catalog.Kinds);
    }

    [Fact]
    public async Task BuiltInNames_KeepTheirTranslationKey_AfterSaving_ButRenamedOnesDoNot()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();
        var defaults = await service.GetDefinitionsAsync(TestContext.Current.CancellationToken);
        Assert.All(defaults, d => Assert.Equal("FileKind." + d.Key, d.LabelKey));

        await service.SaveAsync(
        [
            Edit("folder", "フォルダ", "📁"),
            Edit("file", "その他", "❔"),
            Edit("pdf", "PDFファイル", "📕", "pdf"),
            Edit("text", "メモ", "📝", "txt"),
        ], TestContext.Current.CancellationToken);
        var catalog = await service.GetCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal("FileKind.folder", catalog.Folder.LabelKey);
        Assert.Null(catalog.Generic.LabelKey);
        Assert.Equal("FileKind.pdf", catalog.For("pdf", false).LabelKey);
        Assert.Null(catalog.For("txt", false).LabelKey);
    }

    [Fact]
    public async Task ManuallyCorruptedRows_DoNotBreakTheCatalog()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // フォルダ・汎用の行が無く、同じ拡張子が 2 つの種類にある（検証を通さずに DB を直接編集した状態）。
        db.FileKindSettings.AddRange(
            new FileKindSetting { Key = "first", Label = "先", Icon = "1", Extensions = "dup,only1", SortOrder = 0 },
            new FileKindSetting { Key = "second", Label = "後", Icon = "2", Extensions = "dup,only2", SortOrder = 1 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var catalog = await service.GetCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal("first", catalog.For("dup", false).Id);       // 先に定義された種類が優先
        Assert.Equal("second", catalog.For("only2", false).Id);
        Assert.Equal("📁", catalog.Folder.Icon);                    // 欠けた行は組み込みの既定値で補う
        Assert.Equal("📃", catalog.Generic.Icon);
        var definitions = await service.GetDefinitionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["folder", "file"], definitions.Take(2).Select(d => d.Key));
    }

    [Fact]
    public async Task ExtensionsWithSurroundingSpacesInTheDatabase_StillMatch()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileKindService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.FileKindSettings.Add(new FileKindSetting { Key = "k", Label = "K", Icon = "k", Extensions = " ABC , def ,, ", SortOrder = 0 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var catalog = await service.GetCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal("k", catalog.For("abc", false).Id);
        Assert.Equal("k", catalog.For("DEF", false).Id);
    }
}
