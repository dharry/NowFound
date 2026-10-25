using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

public class SqlitePragmaInterceptorTests
{
    [Fact]
    public async Task Connection_UsesWalAndBusyTimeout()
    {
        await using var host = await TestHost.CreateAsync(seed: false);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var journal = await db.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToListAsync(TestContext.Current.CancellationToken);
        var timeout = await db.Database.SqlQueryRaw<int>("PRAGMA busy_timeout").ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("wal", journal.Single());
        Assert.Equal(5000, timeout.Single());
    }
}
