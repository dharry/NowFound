using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.FileKinds;
using NowFound.Application.Settings;
using NowFound.Application.Users;

namespace NowFound.UnitTests;

public class SettingsTransferServiceTests
{
    private static readonly string RootA = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "transfer-a"));
    private static readonly string RootB = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "transfer-b"));

    private static async Task<(TestHost Host, IServiceScope Scope)> CreateAsync()
    {
        var host = await TestHost.CreateAsync();
        return (host, host.Services.CreateScope());
    }

    private static T Get<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    private static string FoldersJson(params object[] folders) =>
        JsonSerializer.Serialize(new { format = "nowfound.folders", version = 1, folders });

    [Fact]
    public async Task Folders_ExportedFromOneEnvironment_CanBeImportedIntoAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, sourceScope) = await CreateAsync();
        await using var _ = source;
        using var __ = sourceScope;
        await Get<IAdminSettingsService>(sourceScope).SaveFolderAsync(new FolderEdit
        {
            Name = "社内共有",
            Path = RootA,
            ExcludeHidden = false,
            IsShared = true,
            ShareName = "share$",
            ExcludePatterns = ["*.tmp", "build/"],
        }, ct);
        var json = await Get<ISettingsTransferService>(sourceScope).ExportFoldersAsync(ct);

        var (target, targetScope) = await CreateAsync();
        await using var ___ = target;
        using var ____ = targetScope;
        var result = await Get<ISettingsTransferService>(targetScope).ImportFoldersAsync(json, ct);

        Assert.True(result.Succeeded);
        Assert.Equal((1, 0), (result.Created, result.Updated));
        var admin = Get<IAdminSettingsService>(targetScope);
        var edit = await admin.GetFolderAsync((await admin.ListFoldersAsync(ct)).Single().Id, ct);
        Assert.Equal("社内共有", edit!.Name);
        Assert.Equal(RootA, edit.Path);
        Assert.False(edit.ExcludeHidden);
        Assert.True(edit.IsShared);
        Assert.Equal("share$", edit.ShareName);
        Assert.Equal(["*.tmp", "build/"], edit.ExcludePatterns);
    }

    [Fact]
    public async Task Folders_Export_WritesJapaneseAsIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        await Get<IAdminSettingsService>(scope).SaveFolderAsync(new FolderEdit { Name = "資料", Path = RootA }, ct);

        var json = await Get<ISettingsTransferService>(scope).ExportFoldersAsync(ct);

        Assert.Contains("資料", json);
        Assert.Contains("\"format\": \"nowfound.folders\"", json);
    }

    [Fact]
    public async Task Folders_Import_UpdatesTheFolderWithTheSamePath_AndKeepsOthersAndGroups()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var admin = Get<IAdminSettingsService>(scope);
        await admin.SaveFolderAsync(new FolderEdit { Name = "旧名", Path = RootA, ExcludePatterns = ["old"] }, ct);
        await admin.SaveFolderAsync(new FolderEdit { Name = "残る", Path = RootB }, ct);
        var folderId = (await admin.ListFoldersAsync(ct)).Single(f => f.Path == RootA).Id;
        await admin.SaveGroupAsync(new GroupEdit { Name = "G", FolderIds = [folderId] }, ct);

        var result = await Get<ISettingsTransferService>(scope).ImportFoldersAsync(
            FoldersJson(new { name = "新名", path = RootA + Path.DirectorySeparatorChar, excludePatterns = new[] { "new" } }), ct);

        Assert.True(result.Succeeded);
        Assert.Equal((0, 1), (result.Created, result.Updated));
        var folders = await admin.ListFoldersAsync(ct);
        Assert.Equal(2, folders.Count);
        var updated = folders.Single(f => f.Path == RootA);
        Assert.Equal(folderId, updated.Id);
        Assert.Equal("新名", updated.Name);
        Assert.Equal(1, updated.GroupCount);
        Assert.Equal(["new"], (await admin.GetFolderAsync(folderId, ct))!.ExcludePatterns);
        Assert.Contains(folders, f => f.Name == "残る");
    }

    [Fact]
    public async Task Folders_Import_ChangesNothing_WhenAnyFolderIsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var admin = Get<IAdminSettingsService>(scope);
        await admin.SaveFolderAsync(new FolderEdit { Name = "既存", Path = RootA }, ct);

        var result = await Get<ISettingsTransferService>(scope).ImportFoldersAsync(
            FoldersJson(
                new { name = "既存を上書き", path = RootA },
                new { name = "新規", path = RootB },
                new { name = "相対", path = "relative/path" }), ct);

        Assert.False(result.Succeeded);
        Assert.Equal("Error.Transfer.FolderInvalid", result.Result.Errors[0].Key);
        Assert.Contains(result.Result.Errors, e => e.Key == "Error.Folder.PathNotAbsolute");
        var folders = await admin.ListFoldersAsync(ct);
        Assert.Equal("既存", folders.Single().Name);
    }

    [Fact]
    public async Task Folders_Import_RejectsTheSamePathTwiceInTheFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await Get<ISettingsTransferService>(scope).ImportFoldersAsync(
            FoldersJson(new { name = "A", path = RootA }, new { name = "B", path = RootA + Path.DirectorySeparatorChar }), ct);

        Assert.False(result.Succeeded);
        Assert.Equal("Error.Transfer.FolderDuplicate", result.Result.Errors.Single().Key);
        Assert.Empty(await Get<IAdminSettingsService>(scope).ListFoldersAsync(ct));
    }

    [Fact]
    public async Task Folders_Import_RejectsInvalidExcludePatterns()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await Get<ISettingsTransferService>(scope).ImportFoldersAsync(
            FoldersJson(new { name = "A", path = RootA, excludePatterns = new[] { new string('x', 500) } }), ct);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Result.Errors, e => e.Key == "Error.ExcludePattern.TooLong");
    }

    [Theory]
    [InlineData("not json", "Error.Transfer.InvalidJson")]
    [InlineData("""{"format":"nowfound.system-settings","version":1}""", "Error.Transfer.WrongFormat")]
    [InlineData("null", "Error.Transfer.WrongFormat")]
    [InlineData("""{"format":"nowfound.folders","version":2,"folders":[]}""", "Error.Transfer.UnsupportedVersion")]
    [InlineData("""{"format":"nowfound.folders","version":1,"folders":"x"}""", "Error.Transfer.InvalidJson")]
    public async Task Folders_Import_RejectsFilesInTheWrongShape(string json, string expectedKey)
    {
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await Get<ISettingsTransferService>(scope).ImportFoldersAsync(json, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedKey, result.Result.Errors.Single().Key);
    }

    [Fact]
    public async Task SystemSettings_ExportedFromOneEnvironment_CanBeImportedIntoAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, sourceScope) = await CreateAsync();
        await using var _ = source;
        using var __ = sourceScope;
        var admin = Get<IAdminSettingsService>(sourceScope);
        await admin.SetDefaultPageSizeAsync(250, ct);
        await admin.SetLogPageSizeAsync(300, ct);
        await admin.SetAdminListPageSizeAsync(75, ct);
        await admin.SetIncrementalSearchAsync(new IncrementalSearchSettings(false, 3, 400), ct);
        await admin.SetDefaultExcludePatternsAsync(["*.bak", "cache/"], ct);
        await Get<IAccountPolicyService>(sourceScope).SetAsync(new AccountPolicy(12, false, 3, 90, 5), ct);
        await Get<IActiveDirectorySettingsService>(sourceScope).SetAsync(
            new ActiveDirectorySettings(true, "dc.corp.example.com", 636, DirectorySecurity.Ssl, "corp.example.com"), null, ct);
        var json = await Get<ISettingsTransferService>(sourceScope).ExportSystemSettingsAsync(ct);

        var (target, targetScope) = await CreateAsync();
        await using var ___ = target;
        using var ____ = targetScope;
        var result = await Get<ISettingsTransferService>(targetScope).ImportSystemSettingsAsync(json, ct);

        Assert.True(result.Succeeded);
        var imported = Get<IAdminSettingsService>(targetScope);
        Assert.Equal(250, await imported.GetDefaultPageSizeAsync(ct));
        Assert.Equal(300, await imported.GetLogPageSizeAsync(ct));
        Assert.Equal(75, await imported.GetAdminListPageSizeAsync(ct));
        Assert.Equal(new IncrementalSearchSettings(false, 3, 400), await imported.GetIncrementalSearchAsync(ct));
        Assert.Equal(["*.bak", "cache/"], await imported.GetDefaultExcludePatternsAsync(ct));
        Assert.Equal(new AccountPolicy(12, false, 3, 90, 5), await Get<IAccountPolicyService>(targetScope).GetAsync(ct));
        var ad = await Get<IActiveDirectorySettingsService>(targetScope).GetAsync(ct);
        Assert.Equal(new ActiveDirectorySettings(true, "dc.corp.example.com", 636, DirectorySecurity.Ssl, "corp.example.com"), ad);
    }

    [Fact]
    public async Task SystemSettings_Import_UpdatesTheLiveAccountPolicy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var store = host.Services.GetRequiredService<IAccountPolicyStore>();

        var result = await Get<ISettingsTransferService>(scope).ImportSystemSettingsAsync(
            """{"format":"nowfound.system-settings","version":1,"accountPolicy":{"passwordMinLength":14,"maxFailedAttempts":0}}""", ct);

        Assert.True(result.Succeeded);
        Assert.Equal(14, store.Current.PasswordMinLength);
        Assert.Equal(0, store.Current.MaxFailedAttempts);
    }

    [Fact]
    public async Task SystemSettings_Export_NeverContainsTheBindPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        await Get<IActiveDirectorySettingsService>(scope).SetAsync(
            new ActiveDirectorySettings(false, "dc.corp.example.com", 389, DirectorySecurity.None, "corp.example.com", "svc-read"),
            "S3cret-Bind-Pass", ct);

        var json = await Get<ISettingsTransferService>(scope).ExportSystemSettingsAsync(ct);

        Assert.Contains("svc-read", json);
        Assert.DoesNotContain("S3cret-Bind-Pass", json);
        Assert.DoesNotContain("bindPassword", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemSettings_Import_KeepsTheStoredBindPassword_WhenTheBindUserIsKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var ad = Get<IActiveDirectorySettingsService>(scope);
        await ad.SetAsync(
            new ActiveDirectorySettings(false, "dc.corp.example.com", 389, DirectorySecurity.None, "corp.example.com", "svc-read"),
            "S3cret-Bind-Pass", ct);
        var json = await Get<ISettingsTransferService>(scope).ExportSystemSettingsAsync(ct);

        var result = await Get<ISettingsTransferService>(scope).ImportSystemSettingsAsync(json, ct);

        Assert.True(result.Succeeded);
        Assert.Equal("S3cret-Bind-Pass", await ad.GetBindPasswordAsync(ct));
    }

    [Fact]
    public async Task SystemSettings_Import_AcceptsTheBindUserWithoutAPassword_AndAsksToSetItAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await Get<ISettingsTransferService>(scope).ImportSystemSettingsAsync(
            """{"format":"nowfound.system-settings","version":1,"pageSize":200,"activeDirectory":{"server":"dc.corp.example.com","port":389,"domain":"corp.example.com","bindUser":"svc-read"}}""", ct);

        Assert.True(result.Succeeded);
        Assert.True(result.BindPasswordNeeded);
        Assert.Equal(200, await Get<IAdminSettingsService>(scope).GetDefaultPageSizeAsync(ct));
        var ad = Get<IActiveDirectorySettingsService>(scope);
        Assert.Equal("svc-read", (await ad.GetAsync(ct)).BindUser);
        Assert.False(await ad.HasBindPasswordAsync(ct));
    }

    [Fact]
    public async Task SystemSettings_Import_DoesNotAskForThePassword_WhenOneIsAlreadyStoredOrNoBindUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var transfer = Get<ISettingsTransferService>(scope);

        var withoutBindUser = await transfer.ImportSystemSettingsAsync(
            """{"format":"nowfound.system-settings","version":1,"activeDirectory":{"server":"dc.corp.example.com","port":389,"domain":"corp.example.com"}}""", ct);
        Assert.True(withoutBindUser.Succeeded);
        Assert.False(withoutBindUser.BindPasswordNeeded);

        await Get<IActiveDirectorySettingsService>(scope).SetAsync(
            new ActiveDirectorySettings(false, "dc.corp.example.com", 389, DirectorySecurity.None, "corp.example.com", "svc-read"),
            "S3cret-Bind-Pass", ct);
        var json = await transfer.ExportSystemSettingsAsync(ct);
        var again = await transfer.ImportSystemSettingsAsync(json, ct);
        Assert.True(again.Succeeded);
        Assert.False(again.BindPasswordNeeded);
    }

    [Fact]
    public async Task SystemSettings_Import_ChangesNothing_WhenAnyValueIsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await Get<ISettingsTransferService>(scope).ImportSystemSettingsAsync(
            """{"format":"nowfound.system-settings","version":1,"pageSize":500,"logPageSize":0,"accountPolicy":{"passwordMinLength":2},"incrementalSearch":{"enabled":true,"minChars":2,"delayMs":1}}""", ct);

        Assert.False(result.Succeeded);
        Assert.Equal(
            ["logPageSize", "incrementalSearch", "accountPolicy"],
            result.Result.Errors.Where(e => e.Key == "Error.Transfer.ValueInvalid").Select(e => (string)e.Args[0]!));
        Assert.Equal(SystemDefaults.PageSize, await Get<IAdminSettingsService>(scope).GetDefaultPageSizeAsync(ct));
        Assert.Equal(AccountPolicy.Default, await Get<IAccountPolicyService>(scope).GetAsync(ct));
    }

    [Fact]
    public async Task SystemSettings_Import_ResetsTheDefaultExcludePatterns_WhenNotCustomized()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var admin = Get<IAdminSettingsService>(scope);
        await admin.SetDefaultExcludePatternsAsync(["only"], ct);

        var result = await Get<ISettingsTransferService>(scope).ImportSystemSettingsAsync(
            """{"format":"nowfound.system-settings","version":1,"defaultExcludePatterns":{"customized":false,"patterns":["ignored"]}}""", ct);

        Assert.True(result.Succeeded);
        Assert.False(await admin.IsDefaultExcludePatternsCustomizedAsync(ct));
    }

    [Fact]
    public async Task SystemSettings_Import_LeavesOmittedSectionsAsTheyAre()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var admin = Get<IAdminSettingsService>(scope);
        await admin.SetLogPageSizeAsync(333, ct);

        var result = await Get<ISettingsTransferService>(scope).ImportSystemSettingsAsync(
            """{"format":"nowfound.system-settings","version":1,"pageSize":120}""", ct);

        Assert.True(result.Succeeded);
        Assert.Equal(120, await admin.GetDefaultPageSizeAsync(ct));
        Assert.Equal(333, await admin.GetLogPageSizeAsync(ct));
    }

    [Fact]
    public async Task SystemSettings_Import_RejectsAFoldersFile()
    {
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await Get<ISettingsTransferService>(scope).ImportSystemSettingsAsync(
            FoldersJson(), TestContext.Current.CancellationToken);

        Assert.Equal("Error.Transfer.WrongFormat", result.Result.Errors.Single().Key);
    }

    private static FileKindEdit[] CustomKinds() =>
    [
        new() { Key = "folder", Label = "フォルダ", Icon = "📁" },
        new() { Key = "file", Label = "ファイル", Icon = "📄" },
        new() { Label = "設計書", Icon = "📐", Extensions = "dwg, dxf" },
    ];

    [Fact]
    public async Task FileKinds_ExportedFromOneEnvironment_CanBeImportedIntoAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, sourceScope) = await CreateAsync();
        await using var _ = source;
        using var __ = sourceScope;
        Assert.True((await Get<IFileKindService>(sourceScope).SaveAsync(CustomKinds(), ct)).Succeeded);
        var json = await Get<ISettingsTransferService>(sourceScope).ExportFileKindsAsync(ct);

        var (target, targetScope) = await CreateAsync();
        await using var ___ = target;
        using var ____ = targetScope;
        var result = await Get<ISettingsTransferService>(targetScope).ImportFileKindsAsync(json, ct);

        Assert.True(result.Succeeded);
        var kinds = Get<IFileKindService>(targetScope);
        Assert.True(await kinds.IsCustomizedAsync(ct));
        var imported = await kinds.GetDefinitionsAsync(ct);
        Assert.Equal(3, imported.Count);
        var custom = imported.Single(d => d.Label == "設計書");
        Assert.Equal("📐", custom.Icon);
        Assert.Equal(["dwg", "dxf"], custom.Extensions);
        Assert.Contains("設計書", json);
    }

    [Fact]
    public async Task FileKinds_Export_OfTheBuiltInDefaults_ImportsAsNotCustomized()
    {
        var ct = TestContext.Current.CancellationToken;
        var (source, sourceScope) = await CreateAsync();
        await using var _ = source;
        using var __ = sourceScope;
        var json = await Get<ISettingsTransferService>(sourceScope).ExportFileKindsAsync(ct);
        Assert.Contains("\"customized\": false", json);

        var (target, targetScope) = await CreateAsync();
        await using var ___ = target;
        using var ____ = targetScope;
        var kinds = Get<IFileKindService>(targetScope);
        await kinds.SaveAsync(CustomKinds(), ct);

        var result = await Get<ISettingsTransferService>(targetScope).ImportFileKindsAsync(json, ct);

        Assert.True(result.Succeeded);
        Assert.False(await kinds.IsCustomizedAsync(ct));
    }

    [Fact]
    public async Task FileKinds_Import_KeepsTheCurrentSettings_WhenTheFileIsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var kinds = Get<IFileKindService>(scope);
        await kinds.SaveAsync(CustomKinds(), ct);

        // フォルダの行が無い・同じ拡張子が 2 つの種類にある。
        var result = await Get<ISettingsTransferService>(scope).ImportFileKindsAsync(
            """{"format":"nowfound.file-kinds","version":1,"customized":true,"kinds":[{"key":"file","label":"ファイル","icon":"📄","extensions":[]},{"key":"a","label":"A","icon":"A","extensions":["txt"]},{"key":"b","label":"B","icon":"B","extensions":["txt"]}]}""", ct);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Result.Errors, e => e.Key == "Error.FileKind.Required.Folder");
        Assert.Contains((await kinds.GetDefinitionsAsync(ct)), d => d.Label == "設計書");
    }

    [Theory]
    [InlineData("""{"format":"nowfound.folders","version":1}""", "Error.Transfer.WrongFormat")]
    [InlineData("""{"format":"nowfound.file-kinds","version":1,"customized":true}""", "Error.Transfer.ValueInvalid")]
    [InlineData("""{"format":"nowfound.file-kinds","version":1,"customized":true,"kinds":[null]}""", "Error.Transfer.ValueInvalid")]
    [InlineData("oops", "Error.Transfer.InvalidJson")]
    public async Task FileKinds_Import_RejectsFilesInTheWrongShape(string json, string expectedKey)
    {
        var (host, scope) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await Get<ISettingsTransferService>(scope).ImportFileKindsAsync(json, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedKey, result.Result.Errors.Single().Key);
    }

    private static class SystemDefaults
    {
        public const int PageSize = NowFound.Domain.Entities.SystemSetting.DefaultPageSize;
    }
}
