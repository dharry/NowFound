using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Settings;

namespace NowFound.UnitTests;

public class FolderUncPathTests
{
    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\docs")]
    [InlineData("//server/share")]
    [InlineData(@"  \\server\share  ")]
    public async Task SaveFolder_RejectsUncPaths(string path)
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAdminSettingsService>();

        var result = await service.SaveFolderAsync(new FolderEdit { Name = "共有", Path = path }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("Error.Folder.PathUnc", result.Errors.Single().Key);
        Assert.Empty(await service.ListFoldersAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveFolder_StillAcceptsALocalAbsolutePath()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAdminSettingsService>();
        var path = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);

        var result = await service.SaveFolderAsync(new FolderEdit { Name = "ローカル", Path = path }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ImportFolders_RejectsUncPaths_AndChangesNothing()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var transfer = scope.ServiceProvider.GetRequiredService<ISettingsTransferService>();

        var result = await transfer.ImportFoldersAsync(
            """{"format":"nowfound.folders","version":1,"folders":[{"name":"共有","path":"\\\\server\\share"}]}""",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Result.Errors, e => e.Key == "Error.Folder.PathUnc");
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IAdminSettingsService>().ListFoldersAsync(TestContext.Current.CancellationToken));
    }
}
