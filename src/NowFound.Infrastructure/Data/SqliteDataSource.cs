using Microsoft.Data.Sqlite;

namespace NowFound.Infrastructure.Data;

/// <summary>
/// SQLite 接続文字列の相対パスを、指定した基準ディレクトリからの絶対パスに解決する。
/// サービス実行時は作業ディレクトリが不定（Windows: System32、systemd: /）のため、DB の置き場所を固定するために使う。
/// </summary>
public static class SqliteDataSource
{
    /// <summary>
    /// 接続文字列の <c>Data Source</c> が相対パスなら <paramref name="baseDirectory"/> 基準の絶対パスにし、
    /// 親ディレクトリが無ければ作成する。インメモリ・URI 形式・空の場合は変更しない。
    /// </summary>
    public static string Resolve(string connectionString, string baseDirectory)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource)
            || builder.Mode == SqliteOpenMode.Memory
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        var fullPath = Path.GetFullPath(dataSource, baseDirectory);
        if (Path.GetDirectoryName(fullPath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        builder.DataSource = fullPath;
        return builder.ToString();
    }
}
