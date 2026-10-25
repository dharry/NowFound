using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

/// <summary>新規 DB への同時アクセスや同時書き込みで「database is locked」にならないこと。</summary>
public sealed class SqliteConcurrencyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nowfound-conc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteTestCleanup.DeleteDatabase(_dbPath);
    }

    private AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={_dbPath}")
        .AddInterceptors(new SqlitePragmaInterceptor())
        .Options);

    [Fact]
    public async Task ManyConnectionsOpeningAFreshDatabaseAtOnce_DoNotFail()
    {
        // 新規ファイルに対して、全員が同時に最初の接続を開く（journal_mode=WAL への切り替えが競合しやすい場面）。
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await using var db = NewContext();
            start.Wait();
            return (await db.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToListAsync()).Single();
        })).ToList();

        start.Set();
        var modes = await Task.WhenAll(tasks);

        Assert.All(modes, mode => Assert.Equal("wal", mode));
    }

    [Fact]
    public async Task ConcurrentWriters_AreSerializedByBusyTimeout()
    {
        await using (var setup = NewContext())
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            await using var db = NewContext();
            start.Wait();
            db.SystemSettings.Add(new SystemSetting { Key = $"k{i:D2}", Value = i.ToString() });
            await db.SaveChangesAsync();
        })).ToList();

        start.Set();
        await Task.WhenAll(tasks);

        await using var check = NewContext();
        Assert.Equal(16, await check.SystemSettings.CountAsync(TestContext.Current.CancellationToken));
    }
}
