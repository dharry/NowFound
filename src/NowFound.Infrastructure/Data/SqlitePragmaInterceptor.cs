using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NowFound.Infrastructure.Data;

/// <summary>SQLite 接続時に WAL / busy_timeout / synchronous を設定する。</summary>
public class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    // busy_timeout を最初に設定する。journal_mode=WAL への切り替えはロックを要するため、
    // 先に実行すると、新規 DB への同時アクセスで待たずに「database is locked」になり得る。
    private const string Pragmas = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
