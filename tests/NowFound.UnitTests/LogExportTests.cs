using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Logging;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;
using NowFound.Web.Services;

namespace NowFound.UnitTests;

/// <summary>操作ログの ExportAsync（絞り込み・並び順・上限）と LogExportWriter（形式・無害化）。</summary>
public class LogExportTests
{
    private static readonly string[] Headers = ["Time", "Operation", "User", "Detail", "Ip"];

    private static OperationLogItem Item(string user, string detail = "d", DateTime? at = null, string? ip = "127.0.0.1") =>
        new(1, at ?? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), OperationType.Search, user, detail, ip);

    private static async IAsyncEnumerable<OperationLogItem> Stream(IEnumerable<OperationLogItem> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static async Task<(string Text, int Rows)> WriteAsync(
        IEnumerable<OperationLogItem> items, ExportFormat format = ExportFormat.Csv)
    {
        using var stream = new MemoryStream();
        var rows = await LogExportWriter.WriteAsync(
            stream, Stream(items), format, ExportEncoding.Utf8Bom, Headers, i => i.Operation.ToString(), i => i.Detail,
            TestContext.Current.CancellationToken);
        return (new UTF8Encoding(false).GetString(stream.ToArray()[3..]), rows);
    }

    [Fact]
    public async Task Writer_WritesHeaderAndOneLinePerLog()
    {
        var (text, rows) = await WriteAsync([Item("a@example.com"), Item("b@example.com")]);

        Assert.Equal(2, rows);
        var lines = text.TrimEnd().Split("\r\n");
        Assert.Equal("Time,Operation,User,Detail,Ip", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(",Search,a@example.com,d,127.0.0.1", lines[1]);
    }

    [Fact]
    public async Task Writer_Csv_QuotesAndNeutralizesDetail_Tsv_UsesTabs()
    {
        var items = new[] { Item("a@example.com", detail: "=cmd,\"x\"") };

        var (csv, _) = await WriteAsync(items);
        var (tsv, _) = await WriteAsync(items, ExportFormat.Tsv);

        Assert.Contains("\"'=cmd,\"\"x\"\"\"", csv);
        Assert.Equal(5, tsv.TrimEnd().Split("\r\n")[1].Split('\t').Length);
    }

    [Fact]
    public async Task Writer_WithNoLogs_WritesOnlyTheHeader()
    {
        var (text, rows) = await WriteAsync([]);

        Assert.Equal(0, rows);
        Assert.Equal("Time,Operation,User,Detail,Ip\r\n", text);
    }

    [Fact]
    public void FileName_UsesTheLogPrefixAndFormatExtension()
    {
        var now = new DateTime(2026, 10, 27, 1, 2, 3);

        Assert.Equal("nowfound-log-20261027-010203.csv", LogExportWriter.FileName(ExportFormat.Csv, now));
        Assert.Equal("nowfound-log-20261027-010203.tsv", LogExportWriter.FileName(ExportFormat.Tsv, now));
    }

    [Fact]
    public async Task Service_ExportAsync_AppliesFilterOrderAndMaxRows_IgnoringPaging()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OperationLogDbContext>();
        var baseTime = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            db.OperationLogs.Add(new OperationLog
            {
                Timestamp = baseTime.AddMinutes(i), Operation = OperationType.Login, UserEmail = $"alice{i}@example.com",
            });
        }

        db.OperationLogs.Add(new OperationLog { Timestamp = baseTime, Operation = OperationType.Logout, UserEmail = "bob@example.com" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = scope.ServiceProvider.GetRequiredService<IOperationLogService>();
        var ct = TestContext.Current.CancellationToken;

        // ページサイズ 1・2 ページ目を指定しても、全件（新しい順）が返る。
        var all = await service.ExportAsync(new OperationLogQuery(null, null, OperationType.Login, null, 2, 1), 100, ct).ToListAsync(ct);
        var limited = await service.ExportAsync(new OperationLogQuery(null, null, OperationType.Login, null, 1, 1), 3, ct).ToListAsync(ct);
        var none = await service.ExportAsync(new OperationLogQuery(null, null, null, null, 1, 50), 0, ct).ToListAsync(ct);

        Assert.Equal(5, all.Count);
        Assert.Equal("alice4@example.com", all[0].UserEmail);
        Assert.Equal(["alice4@example.com", "alice3@example.com", "alice2@example.com"], limited.Select(i => i.UserEmail));
        Assert.Empty(none);
    }
}
