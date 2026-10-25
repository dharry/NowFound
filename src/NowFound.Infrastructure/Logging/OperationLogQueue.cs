using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NowFound.Application.Logging;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Logging;

/// <summary>
/// System.Threading.Channels.Channel を用いた操作ログのインメモリバッファキュー。
/// ログ追加時は非ブロッキングでキューに格納し、バックグラウンドまたは明示的な要求時に
/// OperationLogDbContext へのバッチ書き込み（INSERT）を実行する。
/// </summary>
public sealed class OperationLogQueue : IOperationLogQueue
{
    public const int DefaultCapacity = 10_000;
    public const int BatchSize = 100;

    private readonly Channel<OperationLog> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OperationLogQueue> _logger;
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    public OperationLogQueue(
        IServiceScopeFactory scopeFactory,
        ILogger<OperationLogQueue> logger,
        int capacity = DefaultCapacity)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _channel = Channel.CreateBounded<OperationLog>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(OperationLog log)
    {
        return _channel.Writer.TryWrite(log);
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken ct = default)
    {
        return _channel.Reader.WaitToReadAsync(ct);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (_channel.Reader.TryRead(out var first))
            {
                var batch = new List<OperationLog>(BatchSize) { first };
                while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<OperationLogDbContext>();
                db.OperationLogs.AddRange(batch);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or IOException or ObjectDisposedException)
        {
            _logger.LogError(ex, "操作ログのバッチ書き込みに失敗しました");
        }
        finally
        {
            _flushLock.Release();
        }
    }
}
