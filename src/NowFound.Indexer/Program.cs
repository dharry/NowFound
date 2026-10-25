using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NowFound.Indexer;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Indexing;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;

var programName = typeof(IndexerOptions).Assembly.GetName().Name ?? "NowFound.Indexer";
var options = IndexerOptions.Parse(args);

// -h / --help は、他の引数に誤りがあっても使い方を表示して正常終了する。
if (options.ShowHelp)
{
    Console.WriteLine(IndexerOptions.Usage(programName));
    return 0;
}

if (options.Errors.Count > 0)
{
    foreach (var error in options.Errors)
    {
        Console.Error.WriteLine(error);
    }

    Console.Error.WriteLine($"For usage, run: {programName} --help");
    return 1;
}

if (!File.Exists(options.MainDb))
{
    Console.Error.WriteLine($"main.db not found: {options.MainDb}");
    Console.Error.WriteLine($"Specify the Web console's main.db with --main-db (usage: {programName} --help)");
    return 1;
}

// 警告（アクセスできず飛ばした項目）とエラーを logs ディレクトリへ出力する。日ごと（indexer-YYYY-MM-DD.log）にファイルを分け、31 世代を保持する。
var nlogConfig = new LoggingConfiguration();
nlogConfig.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, new FileTarget("file")
{
    FileName = Path.Combine(AppContext.BaseDirectory, "logs", "indexer-${shortdate}.log"),
    Layout = "${longdate} [${level:uppercase=true}] ${logger:shortName=true} ${message} ${exception:format=tostring}",
    MaxArchiveFiles = 31,
    Encoding = System.Text.Encoding.UTF8,
});
using var nlogFactory = new NLog.LogFactory { Configuration = nlogConfig };
using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(new NLogLoggerProvider(new NLogProviderOptions(), nlogFactory)));
var logger = loggerFactory.CreateLogger("Indexer");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var interceptor = new SqlitePragmaInterceptor();
await using var mainDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite($"Data Source={options.MainDb};Mode=ReadOnly")
    .Options);
await using var filesDb = new FilesDbContext(new DbContextOptionsBuilder<FilesDbContext>()
    .UseSqlite($"Data Source={options.FilesDb}")
    .AddInterceptors(interceptor)
    .Options);

try
{
    await filesDb.Database.MigrateAsync(cts.Token);
    var targets = await IndexSynchronizer.LoadTargetsAsync(mainDb, cts.Token);
    logger.LogInformation("Indexer started ({Mode})", options.Full ? "full" : "fast");
    Console.WriteLine($"Target folders: {targets.Count} ({(options.Full ? "full mode" : "fast mode")})");

    var result = await new IndexSynchronizer(new FileScanner(loggerFactory.CreateLogger<FileScanner>())).SyncAllAsync(
        filesDb,
        targets,
        options.Full,
        (target, folderResult, fullScan) => Console.WriteLine(
            $"  [{(fullScan ? "full" : "fast")}] {target.Path}: added {folderResult.Added} / updated {folderResult.Updated} / "
            + $"removed {folderResult.Removed} / unchanged {folderResult.Unchanged} / skipped {folderResult.SkippedDirectories} directories"),
        cts.Token);
    Console.WriteLine($"Done: added {result.Added} / updated {result.Updated} / removed {result.Removed} / unchanged {result.Unchanged}");
    logger.LogInformation(
        "Done: added {Added} / updated {Updated} / removed {Removed} / unchanged {Unchanged}",
        result.Added, result.Updated, result.Removed, result.Unchanged);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Interrupted.");
    logger.LogWarning("Interrupted.");
    return 2;
}
catch (Exception ex)
{
    logger.LogError(ex, "Indexer terminated abnormally");
    Console.Error.WriteLine($"Error: {ex.Message} (see the log in the logs directory for details)");
    return 3;
}
finally
{
    // バッファを書き出してログファイルを閉じる。
    nlogFactory.Shutdown();
}
