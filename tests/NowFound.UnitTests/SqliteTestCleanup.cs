using Microsoft.Data.Sqlite;

namespace NowFound.UnitTests;

internal static class SqliteTestCleanup
{
    /// <summary>
    /// 指定した DB の接続プールだけを閉じて、ファイルを削除できるようにする。
    /// SqliteConnection.ClearAllPools() はプロセス全体のプールを閉じるため、並列で動く他のテストが
    /// 使用中の接続まで破棄してしまう（「SafeHandle が破棄されている」「database is locked」の原因になる）。
    /// </summary>
    public static void ReleasePool(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        SqliteConnection.ClearPool(connection);
    }

    public static void DeleteDatabase(string dbPath)
    {
        ReleasePool(dbPath);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(dbPath + suffix);
        }
    }
}
