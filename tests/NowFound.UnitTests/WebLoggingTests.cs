using Microsoft.Extensions.Logging;
using NowFound.Web.Logging;
using NLog.Extensions.Logging;

namespace NowFound.UnitTests;

public sealed class WebLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nowfound-weblog-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void LogDirectory_IsLogsUnderContentRoot() =>
        Assert.Equal(Path.Combine("root", "logs"), WebLogging.LogDirectory("root"));

    [Fact]
    public void Configuration_WritesInformationAndExceptions_ToDailyFileUnderLogs()
    {
        var logDirectory = WebLogging.LogDirectory(_root);
        using (var factory = new NLog.LogFactory { Configuration = WebLogging.CreateConfiguration(logDirectory) })
        using (var loggers = LoggerFactory.Create(b => b.AddProvider(new NLogLoggerProvider(new NLogProviderOptions(), factory))))
        {
            var logger = loggers.CreateLogger("NowFound.Web");
            logger.LogInformation("NowFound.Web started");
            logger.LogError(new InvalidOperationException("boom"), "unhandled");
            factory.Shutdown();
        }

        var file = Assert.Single(Directory.GetFiles(logDirectory, "web-*.log"));
        var text = File.ReadAllText(file);
        Assert.Contains("[INFO]", text);
        Assert.Contains("NowFound.Web started", text);
        Assert.Contains("[ERROR]", text);
        Assert.Contains("InvalidOperationException: boom", text);
    }
}
