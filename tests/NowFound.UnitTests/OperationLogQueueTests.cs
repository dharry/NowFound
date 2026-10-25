using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NowFound.Application.Logging;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Logging;

namespace NowFound.UnitTests;

/// <summary>
/// 操作ログの非同期 Channel キューおよびバックグラウンドバッチ書き込みのテスト。
/// </summary>
public sealed class OperationLogQueueTests
{
    private static OperationLog CreateLog(string email, OperationType op = OperationType.Login) =>
        new()
        {
            Timestamp = DateTime.UtcNow,
            Operation = op,
            UserEmail = email,
            IpAddress = "127.0.0.1",
        };

    [Fact]
    public async Task Enqueue_DoesNotWriteToDb_UntilFlushed()
    {
        await using var host = await TestHost.CreateAsync(seed: false);
        var queue = host.Services.GetRequiredService<IOperationLogQueue>();
        var db = host.Services.GetRequiredService<OperationLogDbContext>();

        // キューに 3 件エンキュー
        Assert.True(queue.TryEnqueue(CreateLog("user1@example.com")));
        Assert.True(queue.TryEnqueue(CreateLog("user2@example.com")));
        Assert.True(queue.TryEnqueue(CreateLog("user3@example.com")));

        Assert.Equal(3, queue.Count);

        // まだ FlushAsync を呼んでいないため、DB には 0 件
        var countBefore = await db.OperationLogs.CountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, countBefore);

        // フラッシュ実行
        await queue.FlushAsync(TestContext.Current.CancellationToken);

        // キューは空になり、DB に 3 件保存されていること
        Assert.Equal(0, queue.Count);
        var countAfter = await db.OperationLogs.CountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, countAfter);
    }

    [Fact]
    public async Task Channel_DropOldest_WhenExceedingCapacity()
    {
        await using var host = await TestHost.CreateAsync(seed: false);
        var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();

        // 容量 3 の小さなキューを作成
        var smallQueue = new OperationLogQueue(scopeFactory, NullLogger<OperationLogQueue>.Instance, capacity: 3);

        smallQueue.TryEnqueue(CreateLog("user1@example.com"));
        smallQueue.TryEnqueue(CreateLog("user2@example.com"));
        smallQueue.TryEnqueue(CreateLog("user3@example.com"));
        // 4件目を追加すると最古の user1 がドロップされる
        smallQueue.TryEnqueue(CreateLog("user4@example.com"));

        Assert.Equal(3, smallQueue.Count);

        await smallQueue.FlushAsync(TestContext.Current.CancellationToken);

        var db = host.Services.GetRequiredService<OperationLogDbContext>();
        var emails = await db.OperationLogs.OrderBy(l => l.Id).Select(l => l.UserEmail).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["user2@example.com", "user3@example.com", "user4@example.com"], emails);
    }

    [Fact]
    public async Task BackgroundService_FlushesAutomatically_WithinBatchDelay()
    {
        await using var host = await TestHost.CreateAsync(seed: false);
        var queue = host.Services.GetRequiredService<IOperationLogQueue>();
        var db = host.Services.GetRequiredService<OperationLogDbContext>();

        // BatchDelay 50ms のバックグラウンドサービスを開始
        var bgService = new OperationLogBackgroundService(queue, NullLogger<OperationLogBackgroundService>.Instance)
        {
            BatchDelay = TimeSpan.FromMilliseconds(50),
        };

        using var cts = new CancellationTokenSource();
        var bgTask = bgService.StartAsync(cts.Token);

        // ログを追加
        queue.TryEnqueue(CreateLog("async1@example.com"));
        queue.TryEnqueue(CreateLog("async2@example.com"));

        // バックグラウンドでのフラッシュを待つ（最大 2 秒ポーリング）
        var written = false;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            if (await db.OperationLogs.CountAsync(TestContext.Current.CancellationToken) == 2)
            {
                written = true;
                break;
            }
        }

        Assert.True(written, "バックグラウンドワーカーによってログがDBに書き込まれること");

        await bgService.StopAsync(CancellationToken.None);
        await bgTask;
    }

    [Fact]
    public async Task BackgroundService_StopAsync_FlushesRemainingLogs()
    {
        await using var host = await TestHost.CreateAsync(seed: false);
        var queue = host.Services.GetRequiredService<IOperationLogQueue>();
        var db = host.Services.GetRequiredService<OperationLogDbContext>();

        // 長い BatchDelay（1分）を設定し、自動フラッシュ前に停止させる
        var bgService = new OperationLogBackgroundService(queue, NullLogger<OperationLogBackgroundService>.Instance)
        {
            BatchDelay = TimeSpan.FromMinutes(1),
        };

        using var cts = new CancellationTokenSource();
        await bgService.StartAsync(cts.Token);

        queue.TryEnqueue(CreateLog("shutdown@example.com"));
        Assert.Equal(1, queue.Count);

        // サービスを停止 -> StopAsync で残余ログがフラッシュされること
        await bgService.StopAsync(CancellationToken.None);

        Assert.Equal(0, queue.Count);
        var count = await db.OperationLogs.CountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OperationLogService_WriteAsync_And_SearchAsync_Integration()
    {
        await using var host = await TestHost.CreateAsync(seed: false);
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOperationLogService>();

        // WriteAsync は非ブロッキングでキューに積む
        await service.WriteAsync(new OperationLogEntry(
            OperationType.Search, "u1", "u1@example.com", null, "192.168.1.10"), TestContext.Current.CancellationToken);

        // SearchAsync を呼ぶとキューが自動的にフラッシュされ、直近のログが取得できること
        var page = await service.SearchAsync(
            new OperationLogQuery(null, null, OperationType.Search, null, 1, 10), TestContext.Current.CancellationToken);

        var item = Assert.Single(page.Items);
        Assert.Equal("u1@example.com", item.UserEmail);
        Assert.Equal(OperationType.Search, item.Operation);
        Assert.Equal("192.168.1.10", item.IpAddress);
    }
}
