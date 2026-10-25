using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain.Entities;

namespace NowFound.UnitTests;

public class AdminSettingsServiceTests
{
    private static readonly string AbsRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "data"));

    private static async Task<(TestHost Host, IServiceScope Scope, IAdminSettingsService Service)> CreateAsync()
    {
        var host = await TestHost.CreateAsync();
        var scope = host.Services.CreateScope();
        return (host, scope, scope.ServiceProvider.GetRequiredService<IAdminSettingsService>());
    }

    [Fact]
    public async Task SaveFolder_CreateAndRead_NormalizesPatterns()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await service.SaveFolderAsync(new FolderEdit
        {
            Name = " Docs ",
            Path = AbsRoot + Path.DirectorySeparatorChar,
            ExcludePatterns = ["*.tmp", " *.tmp ", "", "node_modules"],
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var summary = Assert.Single(await service.ListFoldersAsync(TestContext.Current.CancellationToken));
        var folder = (await service.GetFolderAsync(summary.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal("Docs", folder.Name);
        Assert.Equal(AbsRoot, folder.Path);
        Assert.Equal(["*.tmp", "node_modules"], folder.ExcludePatterns);
        Assert.True(folder.ExcludeHidden);
    }

    [Fact]
    public async Task SaveFolder_Update_ReplacesPatterns()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        await service.SaveFolderAsync(new FolderEdit { Name = "A", Path = AbsRoot, ExcludePatterns = ["*.tmp"] }, TestContext.Current.CancellationToken);
        var id = (await service.ListFoldersAsync(TestContext.Current.CancellationToken)).Single().Id;

        var result = await service.SaveFolderAsync(new FolderEdit
        {
            Id = id, Name = "A2", Path = AbsRoot, ExcludeHidden = false, ExcludePatterns = ["*.bak"],
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var folder = (await service.GetFolderAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal("A2", folder.Name);
        Assert.False(folder.ExcludeHidden);
        Assert.Equal(["*.bak"], folder.ExcludePatterns);
    }

    [Theory]
    [InlineData("", "C:\\x", "Error.Folder.NameAndPathRequired")]
    [InlineData("n", "", "Error.Folder.NameAndPathRequired")]
    [InlineData("n", "relative/path", "Error.Folder.PathNotAbsolute")]
    public async Task SaveFolder_InvalidInput_Fails(string name, string path, string expectedKey)
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await service.SaveFolderAsync(new FolderEdit { Name = name, Path = path }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedKey, result.Errors.Single().Key);
    }

    [Fact]
    public async Task SaveFolder_DuplicatePath_Fails()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        await service.SaveFolderAsync(new FolderEdit { Name = "A", Path = AbsRoot }, TestContext.Current.CancellationToken);

        var result = await service.SaveFolderAsync(new FolderEdit { Name = "B", Path = AbsRoot }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("Error.Folder.PathDuplicate", result.Errors.Single().Key);
    }

    [Fact]
    public async Task SaveGroup_AssignsUsersAndFolders_IgnoringUnknownIds()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var userId = (await users.ListAsync(TestContext.Current.CancellationToken)).Single().Id;
        await service.SaveFolderAsync(new FolderEdit { Name = "A", Path = AbsRoot }, TestContext.Current.CancellationToken);
        var folderId = (await service.ListFoldersAsync(TestContext.Current.CancellationToken)).Single().Id;

        var result = await service.SaveGroupAsync(new GroupEdit
        {
            Name = "G1", UserIds = [userId, "ghost"], FolderIds = [folderId, 999],
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var groupId = (await service.ListGroupsAsync(TestContext.Current.CancellationToken)).Single(g => !g.IsDefault).Id;
        var group = (await service.GetGroupAsync(groupId, TestContext.Current.CancellationToken))!;
        Assert.Equal([userId], group.UserIds);
        Assert.Equal([folderId], group.FolderIds);

        group.FolderIds.Clear();
        await service.SaveGroupAsync(group, TestContext.Current.CancellationToken);
        Assert.Empty((await service.GetGroupAsync(groupId, TestContext.Current.CancellationToken))!.FolderIds);
    }

    [Fact]
    public async Task SaveGroup_DuplicateOrEmptyName_Fails()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        await service.SaveGroupAsync(new GroupEdit { Name = "G1" }, TestContext.Current.CancellationToken);

        var duplicate = await service.SaveGroupAsync(new GroupEdit { Name = "G1" }, TestContext.Current.CancellationToken);
        var blank = await service.SaveGroupAsync(new GroupEdit { Name = " " }, TestContext.Current.CancellationToken);
        Assert.Equal("Error.Group.NameDuplicate", duplicate.Errors.Single().Key);
        Assert.Equal("Error.Group.NameRequired", blank.Errors.Single().Key);
    }

    [Fact]
    public async Task Delete_FolderAndGroup_RemoveRelations()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        await service.SaveFolderAsync(new FolderEdit { Name = "A", Path = AbsRoot, ExcludePatterns = ["x"] }, TestContext.Current.CancellationToken);
        var folderId = (await service.ListFoldersAsync(TestContext.Current.CancellationToken)).Single().Id;
        await service.SaveGroupAsync(new GroupEdit { Name = "G", FolderIds = [folderId] }, TestContext.Current.CancellationToken);
        var groupId = (await service.ListGroupsAsync(TestContext.Current.CancellationToken)).Single(g => !g.IsDefault).Id;

        await service.DeleteFolderAsync(folderId, TestContext.Current.CancellationToken);

        Assert.Empty(await service.ListFoldersAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, (await service.ListGroupsAsync(TestContext.Current.CancellationToken)).Single(g => !g.IsDefault).FolderCount);

        await service.DeleteGroupAsync(groupId, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(await service.ListGroupsAsync(TestContext.Current.CancellationToken), g => !g.IsDefault);
    }

    [Fact]
    public async Task PageSize_DefaultsTo500_AndValidatesRange()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        Assert.Equal(SystemSetting.DefaultPageSize, await service.GetDefaultPageSizeAsync(TestContext.Current.CancellationToken));
        Assert.True((await service.SetDefaultPageSizeAsync(50, TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await service.SetDefaultPageSizeAsync(75, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(75, await service.GetDefaultPageSizeAsync(TestContext.Current.CancellationToken));
        var outOfRange = await service.SetDefaultPageSizeAsync(0, TestContext.Current.CancellationToken);
        Assert.Equal("Error.PageSize.Range", outOfRange.Errors.Single().Key);
        Assert.Equal([PageSizeLimits.Min, PageSizeLimits.Max], outOfRange.Errors.Single().Args);
        Assert.False((await service.SetDefaultPageSizeAsync(PageSizeLimits.Max + 1, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(75, await service.GetDefaultPageSizeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LogPageSize_DefaultsTo100_IsIndependentOfTheSearchPageSize_AndValidatesRange()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(SystemSetting.DefaultLogPageSize, await service.GetLogPageSizeAsync(ct));
        Assert.True((await service.SetLogPageSizeAsync(20, ct)).Succeeded);
        Assert.True((await service.SetDefaultPageSizeAsync(75, ct)).Succeeded);

        Assert.Equal(20, await service.GetLogPageSizeAsync(ct));
        Assert.Equal(75, await service.GetDefaultPageSizeAsync(ct));
        Assert.False((await service.SetLogPageSizeAsync(0, ct)).Succeeded);
        Assert.False((await service.SetLogPageSizeAsync(PageSizeLimits.Max + 1, ct)).Succeeded);
        Assert.Equal(20, await service.GetLogPageSizeAsync(ct));
    }

    [Fact]
    public async Task UserPageSize_SetAndClear()
    {
        var (host, scope, _) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var userId = (await users.ListAsync(TestContext.Current.CancellationToken)).Single().Id;

        Assert.Null(await users.GetPageSizeAsync(userId, TestContext.Current.CancellationToken));
        Assert.True((await users.SetPageSizeAsync(userId, 20, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(20, await users.GetPageSizeAsync(userId, TestContext.Current.CancellationToken));
        Assert.False((await users.SetPageSizeAsync(userId, -1, TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await users.SetPageSizeAsync(userId, null, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Null(await users.GetPageSizeAsync(userId, TestContext.Current.CancellationToken));
        Assert.Equal("Error.UserNotFound", (await users.SetPageSizeAsync("ghost", 10, TestContext.Current.CancellationToken)).Errors.Single().Key);
    }
}
