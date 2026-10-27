using NowFound.Domain.Entities;

namespace NowFound.Application.Logging;

/// <param name="Detail">内容。キー＋引数で保存し、表示時に翻訳する。内容がない操作は null。</param>
public sealed record OperationLogEntry(
    OperationType Operation, string? UserId, string UserEmail, LocalizedMessage? Detail, string? IpAddress);

/// <param name="Detail">旧形式のログの文章。DetailKey があるときは使わない。</param>
public sealed record OperationLogItem(
    long Id, DateTime Timestamp, OperationType Operation, string UserEmail, string Detail, string? IpAddress,
    string? DetailKey = null, IReadOnlyList<string>? DetailArgs = null)
{
    /// <summary>表示時に翻訳する内容か（false なら Detail をそのまま表示する）。</summary>
    public bool HasDetailKey => !string.IsNullOrEmpty(DetailKey);
}

/// <summary>検索条件。From / To は日付（To はその日を含む）。Sort は LogSortKeys（未指定は日時の新しい順）。</summary>
public sealed record OperationLogQuery(
    DateTime? From, DateTime? To, OperationType? Operation, string? User, int Page, int PageSize,
    string? Sort = null, bool Descending = false,
    TimeZoneInfo? TimeZone = null);

public sealed record OperationLogPage(IReadOnlyList<OperationLogItem> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public interface IOperationLogService
{
    /// <summary>ログの書き込み失敗で本来の処理を止めないよう、例外は送出しない。</summary>
    Task WriteAsync(OperationLogEntry entry, CancellationToken ct = default);

    Task<OperationLogPage> SearchAsync(OperationLogQuery query, CancellationToken ct = default);

    /// <summary>検索条件に合うログを、ページングせず並べ替え順に最大 <paramref name="maxRows"/> 件まで 1 件ずつ返す（エクスポート用）。</summary>
    IAsyncEnumerable<OperationLogItem> ExportAsync(OperationLogQuery query, int maxRows, CancellationToken ct = default);

    /// <summary>現在キューに滞留しているログをすべてDBにフラッシュする。</summary>
    Task FlushAsync(CancellationToken ct = default);
}
