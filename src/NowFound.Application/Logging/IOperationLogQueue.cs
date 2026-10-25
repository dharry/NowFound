using NowFound.Domain.Entities;

namespace NowFound.Application.Logging;

/// <summary>
/// 操作ログの非同期インメモリキュー。
/// リクエスト処理スレッドでのディスクI/Oを回避し、バックグラウンドでのバッチ書き込みを実現する。
/// </summary>
public interface IOperationLogQueue
{
    /// <summary>キューに操作ログエンティティを追加する（非ブロッキング）。</summary>
    bool TryEnqueue(OperationLog log);

    /// <summary>読み取り可能なログが存在するか待機する。</summary>
    ValueTask<bool> WaitToReadAsync(CancellationToken ct = default);

    /// <summary>現在キューに滞留しているログをすべてDBにフラッシュする。</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>現在キューに滞留しているログの件数。</summary>
    int Count { get; }
}
