using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NowFound.Application.Indexing;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Indexing;

namespace NowFound.UnitTests;

public sealed class IndexingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nowfound-idx-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nowfound-files-{Guid.NewGuid():N}.db");
    private FilesDbContext _db = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _db = new FilesDbContext(new DbContextOptionsBuilder<FilesDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options);
        await _db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        Directory.Delete(_root, recursive: true);
        SqliteTestCleanup.DeleteDatabase(_dbPath);
    }

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private IndexTarget Target(bool excludeHidden = true, params string[] patterns) =>
        new(1, _root, excludeHidden, patterns);

    private static IndexSynchronizer Synchronizer() => new(new FileScanner(), batchSize: 2);

    [Fact]
    public void Scan_ReturnsFilesAndFolders_WithMetadata()
    {
        Write("docs/Report.TXT", "hello");

        var entries = new FileScanner().Scan(Target()).ToList();

        var file = Assert.Single(entries, e => e.Name == "Report.TXT");
        Assert.False(file.IsDirectory);
        Assert.Equal(5, file.Size);
        Assert.Equal("txt", file.Extension);
        Assert.Equal(Path.Combine(_root, "docs"), file.FolderPath);
        var folder = Assert.Single(entries, e => e.Name == "docs");
        Assert.True(folder.IsDirectory);
        Assert.Null(folder.Size);
    }

    [Fact]
    public void Scan_AppliesExcludePatternsAndHidden()
    {
        Write("keep.txt");
        Write("skip.tmp");
        Write("node_modules/lib.js");
        Write(".hidden/secret.txt");

        var names = new FileScanner().Scan(Target(true, "*.tmp", "node_modules")).Select(e => e.Name).ToList();

        Assert.Contains("keep.txt", names);
        Assert.DoesNotContain("skip.tmp", names);
        Assert.DoesNotContain("lib.js", names);
        Assert.DoesNotContain("secret.txt", names);

        var withHidden = new FileScanner().Scan(Target(false)).Select(e => e.Name).ToList();
        Assert.Contains("secret.txt", withHidden);
    }

    [Fact]
    public void Scan_MissingRoot_ReturnsNothing()
    {
        var target = new IndexTarget(1, Path.Combine(_root, "nope"), true, []);

        Assert.Empty(new FileScanner().Scan(target));
    }

    [Fact]
    public async Task Sync_FirstRun_AddsEverything()
    {
        Write("a.txt");
        Write("b/c.txt");
        Write("b/d.txt");

        var result = await Synchronizer().SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(new SyncResult(4, 0, 0, 0), result);
        Assert.Equal(4, await _db.Files.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sync_SecondRunWithoutChanges_ChangesNothing()
    {
        Write("a.txt");
        await Synchronizer().SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        var result = await Synchronizer().SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(new SyncResult(0, 0, 0, 1), result);
    }

    [Fact]
    public async Task Sync_DetectsAddUpdateAndDelete()
    {
        var a = Write("a.txt", "1");
        var b = Write("b.txt", "1");
        await Synchronizer().SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        File.WriteAllText(a, "longer content");
        File.Delete(b);
        Write("c.txt");
        var result = await Synchronizer().SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(new SyncResult(1, 1, 1, 0), result);
        Assert.Equal(14, (await _db.Files.SingleAsync(f => f.Name == "a.txt", TestContext.Current.CancellationToken)).Size);
        Assert.False(await _db.Files.AnyAsync(f => f.Name == "b.txt", TestContext.Current.CancellationToken));
        Assert.True(await _db.Files.AnyAsync(f => f.Name == "c.txt", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sync_MissingRoot_KeepsExistingEntries()
    {
        Write("a.txt");
        await Synchronizer().SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        var missing = new IndexTarget(1, Path.Combine(_root, "gone"), true, []);
        var result = await Synchronizer().SyncAsync(_db, missing, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, await _db.Files.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SyncAll_RemovesEntriesOfDeletedFolders()
    {
        Write("a.txt");
        await Synchronizer().SyncAllAsync(_db, [Target()], ct: TestContext.Current.CancellationToken);

        var result = await Synchronizer().SyncAllAsync(_db, [], ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Removed);
        Assert.Equal(0, await _db.Files.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fts_FindsPartialNameAndStaysInSyncOnUpdateDelete()
    {
        var a = Write("quarterly-report.txt", "1");
        await Synchronizer().SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, await CountFtsAsync("report"));

        File.Delete(a);
        await Synchronizer().SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountFtsAsync("report"));
    }

    private async Task<int> CountFtsAsync(string term) =>
        (await _db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM FilesFts WHERE FilesFts MATCH {'"' + term + '"'}")
            .ToListAsync(TestContext.Current.CancellationToken)).Single();

    [Fact]
    public async Task Sync_ReplacingFileWithDirectory_UpdatesIsDirectoryAndExtension()
    {
        var filePath = Write("target.txt", "hello world");
        await Synchronizer().SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        var fileEntry = await _db.Files.AsNoTracking().SingleAsync(f => f.Name == "target.txt", TestContext.Current.CancellationToken);
        Assert.False(fileEntry.IsDirectory);
        Assert.Equal("txt", fileEntry.Extension);
        Assert.Equal(11, fileEntry.Size);

        // ファイルを削除し、同名のディレクトリを作成
        File.Delete(filePath);
        Directory.CreateDirectory(filePath);

        var result = await Synchronizer().SyncAsync(_db, Target(), full: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Updated);
        var updatedEntry = await _db.Files.AsNoTracking().SingleAsync(f => f.Name == "target.txt", TestContext.Current.CancellationToken);
        Assert.True(updatedEntry.IsDirectory);
        Assert.Equal(string.Empty, updatedEntry.Extension);
        Assert.Null(updatedEntry.Size);
    }

    [Fact]
    public async Task Sync_ReplacingDirectoryWithFile_UpdatesIsDirectoryAndExtension()
    {
        var dirPath = Path.Combine(_root, "folder.data");
        Directory.CreateDirectory(dirPath);
        await Synchronizer().SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        var dirEntry = await _db.Files.AsNoTracking().SingleAsync(f => f.Name == "folder.data", TestContext.Current.CancellationToken);
        Assert.True(dirEntry.IsDirectory);
        Assert.Equal(string.Empty, dirEntry.Extension);
        Assert.Null(dirEntry.Size);

        // ディレクトリを削除し、同名のファイルを作成
        Directory.Delete(dirPath);
        File.WriteAllText(dirPath, "sample");

        var result = await Synchronizer().SyncAsync(_db, Target(), full: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Updated);
        var updatedEntry = await _db.Files.AsNoTracking().SingleAsync(f => f.Name == "folder.data", TestContext.Current.CancellationToken);
        Assert.False(updatedEntry.IsDirectory);
        Assert.Equal("data", updatedEntry.Extension);
        Assert.Equal(6, updatedEntry.Size);
    }

    [Fact]
    public async Task Sync_WhenReadFailsOnSpecificFile_DoesNotDeleteExistingIndex_AndSuppressesStateUpdate()
    {
        Write("ok.txt", "1");
        Write("locked.txt", "2");

        var normalSync = Synchronizer();
        await normalSync.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, await _db.Files.CountAsync(TestContext.Current.CancellationToken));
        var stateBefore = await _db.IndexFolderStates.SingleAsync(s => s.SearchFolderId == 1, TestContext.Current.CancellationToken);
        var lastScanBefore = stateBefore.LastScanUtc;

        // 次回走査で locked.txt の読み取り時に例外が発生
        var failingSync = new IndexSynchronizer(new FailingFileScanner("locked.txt"));
        var result = await failingSync.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Removed);
        // locked.txt は誤削除されずに残る
        var files = await _db.Files.AsNoTracking().Select(f => f.Name).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("ok.txt", files);
        Assert.Contains("locked.txt", files);

        // エラー発生時は LastScanUtc が更新されず、再試行のために維持される
        var stateAfter = await _db.IndexFolderStates.SingleAsync(s => s.SearchFolderId == 1, TestContext.Current.CancellationToken);
        Assert.Equal(lastScanBefore, stateAfter.LastScanUtc);
    }

    [Fact]
    public async Task Sync_BatchUpdates_CorrectlyUpdatesMultipleEntriesInBatch()
    {
        for (var i = 0; i < 20; i++)
        {
            Write($"file_{i}.txt", "content");
        }

        var synchronizer = new IndexSynchronizer(new FileScanner(), batchSize: 5);
        await synchronizer.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(20, await _db.Files.CountAsync(TestContext.Current.CancellationToken));

        // 全ファイルを更新
        for (var i = 0; i < 20; i++)
        {
            Write($"file_{i}.txt", $"updated_content_longer_{i}");
        }

        var result = await synchronizer.SyncAsync(_db, Target(), full: true, ct: TestContext.Current.CancellationToken);
        Assert.Equal(20, result.Updated);

        var sample = await _db.Files.AsNoTracking().SingleAsync(f => f.Name == "file_0.txt", TestContext.Current.CancellationToken);
        Assert.Equal("updated_content_longer_0".Length, sample.Size);
    }

    [Fact]
    public async Task Sync_DirectoryBecomesEmpty_RemovesAllItsFiles()
    {
        var f1 = Write("sub/f1.txt", "1");
        var f2 = Write("sub/f2.txt", "2");
        var f3 = Write("sub/f3.txt", "3");
        var rootFile = Write("root.txt", "0");

        var synchronizer = Synchronizer();
        await synchronizer.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, await _db.Files.CountAsync(TestContext.Current.CancellationToken)); // sub(dir) + 3 files + rootFile

        // sub 内の全ファイルを削除し、sub は空ディレクトリとして残す
        File.Delete(f1);
        File.Delete(f2);
        File.Delete(f3);

        var result = await synchronizer.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Removed);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated); // sub 自身の更新日時がファイル削除によって変化したため Updated == 1

        var remaining = await _db.Files.AsNoTracking().Select(f => f.Name).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("root.txt", remaining);
        Assert.Contains("sub", remaining);
        Assert.DoesNotContain("f1.txt", remaining);
        Assert.DoesNotContain("f2.txt", remaining);
        Assert.DoesNotContain("f3.txt", remaining);
    }

    [Fact]
    public async Task Sync_DeeplyNestedDirectoryDeleted_CleansUpAllDescendantsViaTempTable()
    {
        Write("tree/level1/level2/level3/deep.txt", "deep");
        Write("tree/level1/level2/other.txt", "other");
        Write("tree/level1/mid.txt", "mid");
        Write("keep/survivor.txt", "keep");

        var synchronizer = Synchronizer();
        await synchronizer.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        // tree/level1 を丸ごと削除
        Directory.Delete(Path.Combine(_root, "tree", "level1"), recursive: true);

        var result = await synchronizer.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);
        // level1(dir), level2(dir), level3(dir), deep.txt, other.txt, mid.txt = 計 6 エントリ削除
        Assert.Equal(6, result.Removed);

        var files = await _db.Files.AsNoTracking().Select(f => f.Name).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("tree", files);
        Assert.Contains("keep", files);
        Assert.Contains("survivor.txt", files);
        Assert.DoesNotContain("level1", files);
        Assert.DoesNotContain("deep.txt", files);
    }

    private sealed class FailingFileScanner(string failingName) : FileScanner
    {
        protected override ScannedEntry? CreateEntry(
            FileSystemInfo child, DirectoryInfo dir, ExcludeMatcher matcher, bool excludeHidden, Stack<DirectoryInfo> pending) =>
            child.Name == failingName
                ? throw new UnauthorizedAccessException("Simulated read access denied")
                : base.CreateEntry(child, dir, matcher, excludeHidden, pending);
    }
}
