using Microsoft.Extensions.Logging;
using NowFound.Application.Indexing;
using NowFound.Infrastructure.Indexing;

namespace NowFound.UnitTests;

public sealed class FileScannerResilienceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nowfound-res-{Guid.NewGuid():N}");

    public FileScannerResilienceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private IndexTarget Target() => new(1, _root, true, []);

    [Fact]
    public void Scan_EntryThatCannotBeRead_IsSkippedWithWarning_AndScanContinues()
    {
        File.WriteAllText(Path.Combine(_root, "ok.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "locked.txt"), "x");
        var logger = new ListLogger();

        var names = new FailingScanner("locked.txt", logger).Scan(Target()).Select(e => e.Name).ToList();

        Assert.Contains("ok.txt", names);
        Assert.DoesNotContain("locked.txt", names);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("locked.txt", warning.Message);
    }

    [Fact]
    public void Scan_AccessibleDirectory_LogsNothing()
    {
        File.WriteAllText(Path.Combine(_root, "ok.txt"), "x");
        var logger = new ListLogger();

        var names = new FileScanner(logger).Scan(Target()).Select(e => e.Name).ToList();

        Assert.Contains("ok.txt", names);
        Assert.Empty(logger.Entries);
    }

    private sealed class FailingScanner(string failingName, ILogger<FileScanner> logger) : FileScanner(logger)
    {
        protected override ScannedEntry? CreateEntry(
            FileSystemInfo child, DirectoryInfo dir, ExcludeMatcher matcher, bool excludeHidden, Stack<DirectoryInfo> pending) =>
            child.Name == failingName
                ? throw new UnauthorizedAccessException("denied")
                : base.CreateEntry(child, dir, matcher, excludeHidden, pending);
    }

    private sealed class ListLogger : ILogger<FileScanner>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
