using System.Globalization;
using System.Text;
using NowFound.Application.Search;

namespace NowFound.Web.Services;

/// <summary>エクスポートの形式。</summary>
public enum ExportFormat
{
    Csv,
    Tsv,
}

/// <summary>エクスポートの文字コード。</summary>
public enum ExportEncoding
{
    /// <summary>UTF-8（BOM 付き）。Excel の日本語環境でそのまま開ける。</summary>
    Utf8Bom,

    /// <summary>Shift_JIS（Windows-31J）。表せない文字は ? になる。</summary>
    ShiftJis,
}

/// <summary>見出しと、種別（ファイル／フォルダ）の表記。</summary>
public sealed record ExportLabels(IReadOnlyList<string> Headers, string File, string Folder);

/// <summary>
/// 検索結果を CSV / TSV として出力先へ 1 行ずつ書き出す。全件をメモリに溜めない。
/// </summary>
public static class SearchExportWriter
{
    /// <summary>この行数ごとに、メモリ上のバッファを出力先へ書き出せるか確かめる。</summary>
    public const int DrainInterval = 100;

    /// <summary>メモリ上のバッファがこのバイト数を超えたら、出力先へ非同期に書き出す。</summary>
    public const int DrainThreshold = 32 * 1024;

    public const int ColumnCount = 9;

    private const string LineEnd = "\r\n";

    static SearchExportWriter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding GetEncoding(ExportEncoding encoding) => encoding switch
    {
        ExportEncoding.ShiftJis => Encoding.GetEncoding(932),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
    };

    public static string ContentType(ExportFormat format, ExportEncoding encoding)
    {
        var charset = encoding == ExportEncoding.ShiftJis ? "shift_jis" : "utf-8";
        return format == ExportFormat.Tsv ? $"text/tab-separated-values; charset={charset}" : $"text/csv; charset={charset}";
    }

    public static string FileName(ExportFormat format, DateTime now) =>
        $"nowfound-{now:yyyyMMdd-HHmmss}.{(format == ExportFormat.Tsv ? "tsv" : "csv")}";

    /// <summary>書き出した行数（見出しを除く）を返す。</summary>
    public static async Task<int> WriteAsync(
        Stream output,
        IAsyncEnumerable<SearchResultItem> items,
        ExportFormat format,
        ExportEncoding encoding,
        ExportLabels labels,
        CancellationToken ct)
    {
        // 出力先（レスポンス）は同期書き込みができない（Kestrel は AllowSynchronousIO = false）。
        // そこで StreamWriter はメモリ上のバッファへ書かせ、溜まったら非同期に出力先へ流す。
        // メモリ上のバッファは StreamWriter が先頭に BOM（UTF-8 のとき）を書ける。
        using var buffer = new MemoryStream();
        using var writer = new StreamWriter(buffer, GetEncoding(encoding), bufferSize: 16 * 1024, leaveOpen: true)
        {
            NewLine = LineEnd,
        };

        var separator = format == ExportFormat.Tsv ? '\t' : ',';
        WriteRow(writer, separator, format, labels.Headers);

        var count = 0;
        var fields = new string?[ColumnCount];
        await foreach (var item in items.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            fields[0] = item.Name;
            fields[1] = item.ShownFolderPath;
            fields[2] = item.CopyPath;
            fields[3] = item.IsDirectory ? labels.Folder : labels.File;
            fields[4] = null; // サイズは数値なので別に書く
            fields[5] = item.Extension;
            fields[6] = item.Owner;
            fields[7] = FormatDate(item.ModifiedAt);
            fields[8] = FormatDate(item.CreatedAt);

            for (var i = 0; i < ColumnCount; i++)
            {
                if (i > 0)
                {
                    writer.Write(separator);
                }

                if (i == 4)
                {
                    // 数値は式として解釈されないので、無害化せずそのまま書く。
                    if (item.Size is { } size)
                    {
                        writer.Write(size.ToString(CultureInfo.InvariantCulture));
                    }
                }
                else
                {
                    writer.Write(Field(fields[i], format));
                }
            }

            writer.Write(LineEnd);
            count++;
            if (count % DrainInterval == 0)
            {
                await DrainAsync(writer, buffer, output, force: false, ct);
            }
        }

        await DrainAsync(writer, buffer, output, force: true, ct);
        await output.FlushAsync(ct);
        return count;
    }

    private static async Task DrainAsync(StreamWriter writer, MemoryStream buffer, Stream output, bool force, CancellationToken ct)
    {
        writer.Flush(); // メモリ上のバッファへ移すだけなので同期でよい。
        if (buffer.Length == 0 || (!force && buffer.Length < DrainThreshold))
        {
            return;
        }

        await output.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ct);
        buffer.SetLength(0);
    }

    private static void WriteRow(StreamWriter writer, char separator, ExportFormat format, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(separator);
            }

            writer.Write(Field(values[i], format));
        }

        writer.Write(LineEnd);
    }

    /// <summary>ローカル時刻の「yyyy-MM-dd HH:mm:ss」。画面の表示と同じ時刻基準。</summary>
    public static string FormatDate(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// 1 つの値を出力用に整える。表計算ソフトで式として実行されないよう、先頭が = + - @ やタブ・改行の値には ' を付ける。
    /// CSV は必要なら " で囲み（RFC 4180）、TSV は区切りを壊すタブ・改行を空白に置き換える。
    /// </summary>
    public static string Field(string? value, ExportFormat format)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
        {
            value = "'" + value;
        }

        if (format == ExportFormat.Tsv)
        {
            return value.Contains('\t') || value.Contains('\r') || value.Contains('\n')
                ? value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')
                : value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
