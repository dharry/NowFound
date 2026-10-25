using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NowFound.Application.Information;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Information;

/// <summary>稼働ホスト・.NET・各 DB の情報を集める。DB への問い合わせは軽い読み取り（バージョン取得と COUNT）だけ。</summary>
public sealed class HostInfoService(AppDbContext mainDb, OperationLogDbContext operationLogDb, FilesDbContext filesDb) : IHostInfoService
{
    public BasicHostInfo GetBasic() => new(Environment.MachineName, CollectRuntime());

    public async Task<HostInfo> GetAsync(CancellationToken ct = default)
    {
        var databases = new List<DatabaseSummary>
        {
            await DescribeAsync("main", mainDb, ct),
            await DescribeAsync("ope", operationLogDb, ct),
            await DescribeAsync("files", filesDb, ct),
        };

        return new HostInfo(CollectHost(), CollectRuntime(), databases);
    }

    public static HostSummary CollectHost() => new(
        Environment.MachineName,
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        ReadCpuName(),
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        Environment.WorkingSet);

    public static RuntimeSummary CollectRuntime() => new(
        RuntimeInformation.FrameworkDescription,
        Environment.Version.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString());

    /// <summary>/proc/cpuinfo の行から CPU 名（先頭の "model name"）を取り出す。無ければ <c>null</c>。</summary>
    public static string? ParseCpuName(IEnumerable<string> cpuInfoLines)
    {
        foreach (var line in cpuInfoLines)
        {
            var separator = line.IndexOf(':');
            if (separator > 0 && line[..separator].Trim().Equals("model name", StringComparison.OrdinalIgnoreCase))
            {
                var name = line[(separator + 1)..].Trim();
                return name.Length > 0 ? name : null;
            }
        }

        return null;
    }

    private static string? ReadCpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim();
            }

            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                return ParseCpuName(File.ReadLines("/proc/cpuinfo"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // CPU 名は参考情報。取れなくても画面全体は表示する。
        }

        return null;
    }

    private static async Task<DatabaseSummary> DescribeAsync(string name, DbContext db, CancellationToken ct)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        string? path = null;
        long? size = null;
        string? version = null;
        List<TableCount> tables = [];

        if (db.Database.GetConnectionString() is { } connectionString)
        {
            var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
            if (!string.IsNullOrWhiteSpace(dataSource) && !dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.GetFullPath(dataSource);
                size = SizeOnDisk(path);
            }
        }

        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version()";
            version = (await command.ExecuteScalarAsync(ct))?.ToString();

            // 件数は COUNT(*) で DB 側に集計させる。テーブル名は EF のモデル由来（利用者の入力ではない）。
            foreach (var table in db.Model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().Distinct().Order())
            {
                command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
                tables.Add(new TableCount(table, Convert.ToInt64(await command.ExecuteScalarAsync(ct))));
            }
        }
        finally
        {
            if (wasClosed)
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        return new DatabaseSummary(name, DisplayProvider(provider), version, path, size, tables);
    }

    private static string DisplayProvider(string provider) =>
        provider.EndsWith("Sqlite", StringComparison.OrdinalIgnoreCase) ? "SQLite" : provider;

    /// <summary>DB 本体と WAL / SHM の合計サイズ。ファイルが無ければ <c>null</c>。</summary>
    private static long? SizeOnDisk(string path)
    {
        long? total = null;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = new FileInfo(path + suffix);
            if (file.Exists)
            {
                total = (total ?? 0) + file.Length;
            }
        }

        return total;
    }
}
