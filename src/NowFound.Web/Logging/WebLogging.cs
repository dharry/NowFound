using NLog.Config;
using NLog.Targets;

namespace NowFound.Web.Logging;

/// <summary>Web コンソールのログ（logs ディレクトリ配下の日別ファイル）の NLog 設定。</summary>
public static class WebLogging
{
    public const string ConfigurationKey = "Logging:File:Enabled";

    /// <summary>ログを出力するディレクトリ。コンテンツルート直下の logs。</summary>
    public static string LogDirectory(string contentRootPath) => Path.Combine(contentRootPath, "logs");

    /// <summary>日別ファイル（web-YYYY-MM-DD.log）に 31 世代まで出力する設定を作る。</summary>
    public static LoggingConfiguration CreateConfiguration(string logDirectory)
    {
        var configuration = new LoggingConfiguration();
        configuration.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, new FileTarget("file")
        {
            FileName = Path.Combine(logDirectory, "web-${shortdate}.log"),
            Layout = "${longdate} [${level:uppercase=true}] ${logger:shortName=true} ${message} ${exception:format=tostring}",
            MaxArchiveFiles = 31,
            Encoding = System.Text.Encoding.UTF8,
        });
        return configuration;
    }
}
