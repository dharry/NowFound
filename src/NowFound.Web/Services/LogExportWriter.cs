using NowFound.Application.Logging;

namespace NowFound.Web.Services;

/// <summary>
/// 操作ログを CSV / TSV として出力先へ 1 行ずつ書き出す。全件をメモリに溜めない。
/// 形式・文字コード・値の無害化は <see cref="SearchExportWriter"/> と共通。
/// </summary>
public static class LogExportWriter
{
    public const int ColumnCount = 5;

    private const string LineEnd = "\r\n";

    public static string FileName(ExportFormat format, DateTime now) =>
        $"nowfound-log-{now:yyyyMMdd-HHmmss}.{(format == ExportFormat.Tsv ? "tsv" : "csv")}";

    /// <summary>書き出した行数（見出しを除く）を返す。</summary>
    /// <param name="headers">見出し（日時・操作・ユーザー・内容・IP アドレス）。</param>
    /// <param name="operationLabel">操作の表示名。</param>
    /// <param name="detail">内容の表示文字列（翻訳済み）。</param>
    public static async Task<int> WriteAsync(
        Stream output,
        IAsyncEnumerable<OperationLogItem> items,
        ExportFormat format,
        ExportEncoding encoding,
        IReadOnlyList<string> headers,
        Func<OperationLogItem, string> operationLabel,
        Func<OperationLogItem, string> detail,
        CancellationToken ct)
    {
        // 出力先は同期書き込みができないので、メモリ上のバッファへ書き、溜まったら非同期に流す。
        using var buffer = new MemoryStream();
        using var writer = new StreamWriter(buffer, SearchExportWriter.GetEncoding(encoding), bufferSize: 16 * 1024, leaveOpen: true)
        {
            NewLine = LineEnd,
        };

        var separator = format == ExportFormat.Tsv ? '\t' : ',';
        WriteRow(writer, separator, format, headers);

        var count = 0;
        var fields = new string?[ColumnCount];
        await foreach (var item in items.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            fields[0] = SearchExportWriter.FormatDate(item.Timestamp);
            fields[1] = operationLabel(item);
            fields[2] = item.UserEmail;
            fields[3] = detail(item);
            fields[4] = item.IpAddress;
            WriteRow(writer, separator, format, fields);
            count++;
            if (count % SearchExportWriter.DrainInterval == 0)
            {
                await SearchExportWriter.DrainAsync(writer, buffer, output, force: false, ct);
            }
        }

        await SearchExportWriter.DrainAsync(writer, buffer, output, force: true, ct);
        await output.FlushAsync(ct);
        return count;
    }

    private static void WriteRow(StreamWriter writer, char separator, ExportFormat format, IReadOnlyList<string?> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(separator);
            }

            writer.Write(SearchExportWriter.Field(values[i], format));
        }

        writer.Write(LineEnd);
    }
}
