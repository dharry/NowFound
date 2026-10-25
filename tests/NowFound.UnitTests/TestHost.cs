using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Infrastructure;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

/// <summary>一時ファイルの SQLite を使ったインフラ層のテストホスト。</summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nowfound-test-{Guid.NewGuid():N}.db");
    private readonly string _opeDbPath = Path.Combine(Path.GetTempPath(), $"nowfound-testope-{Guid.NewGuid():N}.db");
    private readonly string _filesDbPath = Path.Combine(Path.GetTempPath(), $"nowfound-testfiles-{Guid.NewGuid():N}.db");

    public ServiceProvider Services { get; }

    private TestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure($"Data Source={_dbPath}", $"Data Source={_filesDbPath}", $"Data Source={_opeDbPath}");
        Services = services.BuildServiceProvider();
    }

    public static async Task<TestHost> CreateAsync(bool seed = true)
    {
        var host = new TestHost();
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<FilesDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<OperationLogDbContext>().Database.MigrateAsync();
        if (seed)
        {
            await DataSeeder.SeedAsync(
                scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
                scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>());
            await DataSeeder.SeedDefaultGroupAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteTestCleanup.DeleteDatabase(_dbPath);
        SqliteTestCleanup.DeleteDatabase(_filesDbPath);
        SqliteTestCleanup.DeleteDatabase(_opeDbPath);
    }
}
