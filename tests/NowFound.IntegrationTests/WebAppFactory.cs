using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace NowFound.IntegrationTests;

public sealed class WebAppFactory(string? pathBase = null, int? loginRateLimit = 100) : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nowfound-it-{Guid.NewGuid():N}.db");

    private readonly string _opeDbPath = Path.Combine(Path.GetTempPath(), $"nowfound-it-ope-{Guid.NewGuid():N}.db");

    private readonly string _filesDbPath = Path.Combine(Path.GetTempPath(), $"nowfound-it-files-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        if (pathBase is not null)
        {
            builder.UseSetting("PathBase", pathBase);
        }
        if (loginRateLimit is not null)
        {
            builder.UseSetting("RateLimiting:Login:PermitLimit", loginRateLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.UseSetting("Logging:File:Enabled", "false");
        builder.UseSetting("ConnectionStrings:MainDb", $"Data Source={_dbPath}");
        builder.UseSetting("ConnectionStrings:FilesDb", $"Data Source={_filesDbPath}");
        builder.UseSetting("ConnectionStrings:OperationLogDb", $"Data Source={_opeDbPath}");
    }

    public HttpClient CreateBrowserLikeClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // 並列で動く他のテストの接続を巻き込まないよう、このファクトリの DB のプールだけを閉じる。
        foreach (var path in new[] { _dbPath, _filesDbPath, _opeDbPath })
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
                File.Delete(_filesDbPath + suffix);
                File.Delete(_opeDbPath + suffix);
            }
            catch (IOException)
            {
            }
        }
    }
}
