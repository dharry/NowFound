using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NowFound.Application;
using NowFound.Application.Logging;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Logging;

public class OperationLogService(OperationLogDbContext db, IOperationLogQueue queue, ILogger<OperationLogService> logger) : IOperationLogService
{
    public const int MaxDetailLength = 2000;
    public const int MaxSingleArgLength = 500;

    public Task WriteAsync(OperationLogEntry entry, CancellationToken ct = default)
    {
        try
        {
            var log = new OperationLog
            {
                Timestamp = DateTime.UtcNow,
                Operation = entry.Operation,
                UserId = entry.UserId,
                UserEmail = entry.UserEmail,
                DetailKey = entry.Detail?.Key,
                DetailArgs = entry.Detail is { Args.Length: > 0 } detail ? SerializeArgs(detail.FormatArgs()) : null,
                IpAddress = entry.IpAddress,
            };
            queue.TryEnqueue(log);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "操作ログのエンキューに失敗しました: {Operation}", entry.Operation);
        }

        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default) => queue.FlushAsync(ct);

    public static string SerializeArgs(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return "[]";
        }

        // まず各引数の長さを安全な上限に収める（個々の巨大文字列による破綻を防止）
        var truncated = args.Select(a => a.Length > MaxSingleArgLength ? a[..MaxSingleArgLength] + "..." : a).ToList();

        var json = JsonSerializer.Serialize(truncated);
        if (json.Length <= MaxDetailLength)
        {
            return json;
        }

        // 全体シリアライズ長が MaxDetailLength を超える場合は、有効な JSON 配列構造を保ったまま末尾の要素を省略する
        while (truncated.Count > 1 && json.Length > MaxDetailLength)
        {
            truncated.RemoveAt(truncated.Count - 1);
            truncated[^1] = "...";
            json = JsonSerializer.Serialize(truncated);
        }

        // 引数が1要素のみでも MaxDetailLength を超える場合は、その1要素をさらに短縮してシリアライズ
        if (json.Length > MaxDetailLength && truncated.Count == 1)
        {
            var safeLength = Math.Max(10, MaxDetailLength - 20);
            truncated[0] = truncated[0].Length > safeLength ? truncated[0][..safeLength] + "..." : truncated[0];
            json = JsonSerializer.Serialize(truncated);
        }

        return json;
    }

    private static IReadOnlyList<string> ParseArgs(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            // 長すぎて切り詰められた引数は読めないので、引数なしで表示する。
            return [];
        }
    }

    public async Task<OperationLogPage> SearchAsync(OperationLogQuery query, CancellationToken ct = default)
    {
        await queue.FlushAsync(ct);
        var page = Math.Max(1, query.Page);
        var logs = db.OperationLogs.AsNoTracking().AsQueryable();

        var (fromUtc, toUtc) = DateTimeRange.ToUtc(query.From, query.To, query.TimeZone);
        if (fromUtc is { } from)
        {
            logs = logs.Where(l => l.Timestamp >= from);
        }

        if (toUtc is { } to)
        {
            logs = logs.Where(l => l.Timestamp < to);
        }

        if (query.Operation is { } operation)
        {
            logs = logs.Where(l => l.Operation == operation);
        }

        if (!string.IsNullOrWhiteSpace(query.User))
        {
            var pattern = SqlLike.Contains(query.User);
            logs = logs.Where(l => EF.Functions.Like(l.UserEmail, pattern, SqlLike.Escape));
        }

        var total = await logs.CountAsync(ct);
        // 並べ替えの指定がなければ日時の新しい順。同じ値のものは新しい順にそろえる。
        var ordered = (query.Sort, query.Descending) switch
        {
            (LogSortKeys.Time, false) => logs.OrderBy(l => l.Timestamp),
            (LogSortKeys.Operation, false) => logs.OrderBy(l => l.Operation),
            (LogSortKeys.Operation, true) => logs.OrderByDescending(l => l.Operation),
            (LogSortKeys.User, false) => logs.OrderBy(l => l.UserEmail),
            (LogSortKeys.User, true) => logs.OrderByDescending(l => l.UserEmail),
            (LogSortKeys.Ip, false) => logs.OrderBy(l => l.IpAddress),
            (LogSortKeys.Ip, true) => logs.OrderByDescending(l => l.IpAddress),
            _ => logs.OrderByDescending(l => l.Timestamp),
        };
        var items = await ordered.ThenByDescending(l => l.Timestamp).ThenByDescending(l => l.Id)
            .Skip((page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new OperationLogPage(
            [.. items.Select(l => new OperationLogItem(
                l.Id, l.Timestamp, l.Operation, l.UserEmail, l.Detail, l.IpAddress, l.DetailKey, ParseArgs(l.DetailArgs)))],
            page,
            query.PageSize,
            total);
    }
}
