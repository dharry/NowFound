using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Information;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

public class FooterInfoTests
{
    private static Task<FooterInfo> GetAsync(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IFooterInfoService>().GetAsync(TestContext.Current.CancellationToken);

    private static IndexFolderState State(int folderId, DateTime lastScan) =>
        new() { SearchFolderId = folderId, SettingsHash = "h", LastScanUtc = lastScan, LastFullScanUtc = lastScan };

    [Fact]
    public async Task NeverIndexed_HasNoUpdateTime_AndReportsMachineName()
    {
        await using var host = await TestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();

        var info = await GetAsync(scope);

        Assert.Equal(Environment.MachineName, info.MachineName);
        Assert.Null(info.FilesUpdatedUtc);
    }

    [Fact]
    public async Task Indexed_ReturnsLatestScanTimeAcrossFolders()
    {
        await using var host = await TestHost.CreateAsync();
        var older = new DateTime(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 10, 5, 2, 30, 0, DateTimeKind.Utc);
        await using (var seed = host.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<FilesDbContext>();
            db.IndexFolderStates.AddRange(State(1, older), State(2, newer));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = host.Services.CreateAsyncScope();

        Assert.Equal(newer, (await GetAsync(scope)).FilesUpdatedUtc);
    }

    [Fact]
    public async Task Result_IsCachedBetweenCalls()
    {
        await using var host = await TestHost.CreateAsync();
        await using (var first = host.Services.CreateAsyncScope())
        {
            Assert.Null((await GetAsync(first)).FilesUpdatedUtc);
            var db = first.ServiceProvider.GetRequiredService<FilesDbContext>();
            db.IndexFolderStates.Add(State(1, DateTime.UtcNow));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var second = host.Services.CreateAsyncScope();

        Assert.Null((await GetAsync(second)).FilesUpdatedUtc);
    }
}
