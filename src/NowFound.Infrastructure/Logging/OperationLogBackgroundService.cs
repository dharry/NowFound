using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NowFound.Application.Logging;

namespace NowFound.Infrastructure.Logging;

/// <summary>
/// 操作ログのインメモリキューを定期的に監視し、DB へバッチ書き込みを行うバックグラウンドワーカー。
/// アプリケーション終了時は未書き込みログをフラッシュしてから停止する。
/// </summary>
public sealed class OperationLogBackgroundService(
    IOperationLogQueue queue,
    ILogger<OperationLogBackgroundService> logger) : BackgroundService
{
    public static readonly TimeSpan DefaultBatchDelay = TimeSpan.FromMilliseconds(500);

    public TimeSpan BatchDelay { get; init; } = DefaultBatchDelay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await queue.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    // 連続したログ書き込みをバッチ化するため少し待機
                    if (BatchDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(BatchDelay, stoppingToken).ConfigureAwait(false);
                    }

                    await queue.FlushAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "操作ログバックグラウンドワーカーで例外が発生しました");
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // シャットダウン時にキュー内の全残余ログを確実にフラッシュ
            await queue.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "操作ログバックグラウンドワーカーの停止時フラッシュに失敗しました");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
