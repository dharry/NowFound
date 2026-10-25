namespace NowFound.Application.Information;

/// <summary>稼働ホスト（コンピュータ・OS・CPU・メモリ）の情報。</summary>
public sealed record HostSummary(
    string MachineName,
    string OsDescription,
    string OsArchitecture,
    string? CpuName,
    int LogicalProcessors,
    long TotalMemoryBytes,
    long ProcessMemoryBytes);

/// <summary>稼働中の .NET ランタイムの情報。</summary>
public sealed record RuntimeSummary(string FrameworkDescription, string Version, string ProcessArchitecture);

/// <summary>テーブル 1 つ分の行数（<c>SELECT COUNT(*)</c> の結果）。</summary>
public sealed record TableCount(string Table, long Rows);

/// <summary>データベース 1 つ分の情報。<paramref name="Name"/> は main / ope / files。</summary>
public sealed record DatabaseSummary(
    string Name, string Provider, string? EngineVersion, string? Path, long? SizeBytes, IReadOnlyList<TableCount> Tables);

public sealed record HostInfo(HostSummary Host, RuntimeSummary Runtime, IReadOnlyList<DatabaseSummary> Databases);

/// <summary>一般ユーザーにも見せてよい情報（コンピュータ名と .NET）。</summary>
public sealed record BasicHostInfo(string MachineName, RuntimeSummary Runtime);

/// <summary>フッターに出す情報。<paramref name="FilesUpdatedUtc"/> は Indexer が最後に走査を始めた時刻（まだ実行されていなければ <c>null</c>）。</summary>
public sealed record FooterInfo(string MachineName, DateTime? FilesUpdatedUtc);

public interface IFooterInfoService
{
    /// <summary>全ページで呼ばれるため、結果は短時間キャッシュする。</summary>
    Task<FooterInfo> GetAsync(CancellationToken ct = default);
}

public interface IHostInfoService
{
    /// <summary>コンピュータ名と .NET の情報だけ。DB には触れない。</summary>
    BasicHostInfo GetBasic();

    /// <summary>ホスト・DB を含む全情報（管理者向け）。</summary>
    Task<HostInfo> GetAsync(CancellationToken ct = default);
}

/// <summary>バイト数を 1,024 区切りの単位（B / KB / MB / GB / TB）で読みやすくする。</summary>
public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return string.Empty;
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes:N0} B")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:N1} {Units[unit]}");
    }
}
