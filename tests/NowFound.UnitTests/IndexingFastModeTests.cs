using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NowFound.Application.Indexing;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Indexing;

namespace NowFound.UnitTests;

/// <summary>高速モード（ディレクトリ時刻による枝刈り・オーナー遅延取得）とフルモードの挙動。</summary>
public sealed class IndexingFastModeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nowfound-fast-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nowfound-fastdb-{Guid.NewGuid():N}.db");
    private readonly CountingScanner _scanner = new();
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

    private IndexTarget Target(params string[] patterns) => new(1, _root, true, patterns);

    private bool _aged;

    /// <summary>
    /// 既存のディレクトリの更新日時を過去へずらす。ディレクトリの時刻は時計の刻み単位でしか進まないため、
    /// 作成直後は直後の変更が同じ時刻に見えることがある。過去に固定して、変更の検出を決定的にする。
    /// </summary>
    private void AgeDirectories()
    {
        var past = DateTime.UtcNow.AddHours(-1);
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            Directory.SetLastWriteTimeUtc(dir, past);
        }
    }

    /// <summary>テストでは racy window を 0 にして、最初の同期の前にだけディレクトリを過去へずらす。</summary>
    private Task<SyncResult> SyncAsync(bool full = false, params string[] patterns)
    {
        if (!_aged)
        {
            AgeDirectories();
            _aged = true;
        }

        return new IndexSynchronizer(_scanner, batchSize: 2, racyWindow: TimeSpan.Zero).SyncAsync(_db, Target(patterns), full);
    }

    [Fact]
    public async Task RecentlyModifiedDirectories_AreRescanned_UntilTheyAgePastTheRacyWindow()
    {
        // 既定の racy window（2 秒）。作成直後のディレクトリは、時刻が一致していても信用しない。
        Write("sub/a.txt");
        var synchronizer = new IndexSynchronizer(_scanner);
        await synchronizer.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);

        var fresh = await synchronizer.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, fresh.SkippedDirectories);
        Assert.Equal(0, fresh.Added + fresh.Updated + fresh.Removed);

        // 十分に古いディレクトリは、時刻が一致していればスキップされる。
        var aged = DateTime.UtcNow.AddHours(-1);
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "sub"), aged);
        await synchronizer.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);   // 時刻の変化を DB に反映（この回は再走査）
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var skipped = await synchronizer.SyncAsync(_db, Target(), ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, skipped.SkippedDirectories);
    }

    [Fact]
    public async Task RacyWindow_StoresScanStartTime_NotEndTime()
    {
        Write("a.txt");
        var before = DateTime.UtcNow;
        await SyncAsync();
        var state = await _db.IndexFolderStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        Assert.InRange(state.LastScanUtc, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task FastMode_SkipsUnchangedDirectories_AndKeepsTheirEntries()
    {
        Write("sub/a.txt");
        Write("sub/b.txt");
        await SyncAsync();

        var second = await SyncAsync();

        // sub 自身 + a.txt + b.txt は変更なし。sub の中身は読み直していない。
        Assert.Equal(new SyncResult(0, 0, 0, 3, 1), second);
        Assert.Equal(3, await _db.Files.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FastMode_DetectsFileAddedInsideUnchangedParent()
    {
        Write("a/b/old.txt");
        await SyncAsync();

        Write("a/b/new.txt");
        var second = await SyncAsync();

        // a は変化なし（スキップ）、b は時刻が変わったため再走査され、b 自身のエントリも更新される。
        Assert.Equal(1, second.Added);
        Assert.Equal(1, second.Updated);
        Assert.True(await _db.Files.AnyAsync(f => f.Name == "new.txt", TestContext.Current.CancellationToken));
        Assert.Equal(1, second.SkippedDirectories);
    }

    [Fact]
    public async Task FastMode_RefreshesSubdirectoryEntry_SoNextRunIsFullySkipped()
    {
        Write("a/b/old.txt");
        await SyncAsync();
        Write("a/b/new.txt");
        await SyncAsync();

        var third = await SyncAsync();

        Assert.Equal(0, third.Updated);
        Assert.Equal(0, third.Added);
        Assert.Equal(2, third.SkippedDirectories);
    }

    [Fact]
    public async Task FastMode_DetectsDeletedFileAndDeletedDirectory()
    {
        var file = Write("a/x.txt");
        Write("gone/y.txt");
        await SyncAsync();

        File.Delete(file);
        Directory.Delete(Path.Combine(_root, "gone"), recursive: true);
        var second = await SyncAsync();

        Assert.Equal(3, second.Removed);   // x.txt, gone, gone/y.txt
        Assert.Equal(["a"], await _db.Files.Select(f => f.Name).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FastMode_DetectsInPlaceEdit_EvenWhenParentDirectoryTimeIsUnchanged()
    {
        var file = Write("sub/doc.txt", "1");
        await SyncAsync();
        var subDir = Path.Combine(_root, "sub");
        var originalDirTime = Directory.GetLastWriteTimeUtc(subDir);

        File.WriteAllText(file, "now much longer");
        Directory.SetLastWriteTimeUtc(subDir, originalDirTime);   // 親ディレクトリの時刻が動かない状況を再現

        // ハイブリッド差分スキャンにより、親ディレクトリ時刻が不変（SkippedDirectories = 1）でも直下ファイルの更新を即座に検知する
        var fast = await SyncAsync();
        Assert.Equal(1, fast.Updated);
        Assert.Equal(1, fast.SkippedDirectories);
        Assert.Equal(15, (await _db.Rows().SingleAsync(f => f.Name == "doc.txt", TestContext.Current.CancellationToken)).Size);

        // 次の差分スキャンでは更新ゼロ・スキップ継続
        var next = await SyncAsync();
        Assert.Equal(0, next.Updated);
        Assert.Equal(1, next.SkippedDirectories);

        // フルモードでも正常に整合
        var full = await SyncAsync(full: true);
        Assert.Equal(0, full.Updated);
        Assert.Equal(15, (await _db.Rows().SingleAsync(f => f.Name == "doc.txt", TestContext.Current.CancellationToken)).Size);
        Assert.Equal(0, full.SkippedDirectories);
    }

    [Fact]
    public async Task ChangingExcludePatterns_ForcesFullScan()
    {
        Write("sub/keep.txt");
        Write("sub/skip.tmp");
        await SyncAsync();

        var withPattern = await SyncAsync(false, "*.tmp");

        Assert.Equal(1, withPattern.Removed);
        Assert.Equal(0, withPattern.SkippedDirectories);
        Assert.False(await _db.Files.AnyAsync(f => f.Name == "skip.tmp", TestContext.Current.CancellationToken));

        var again = await SyncAsync(false, "*.tmp");
        Assert.Equal(1, again.SkippedDirectories);
    }

    [Fact]
    public async Task ExcludePatternOrder_DoesNotForceFullScan()
    {
        Write("sub/a.txt");
        await SyncAsync(false, "*.tmp", "*.bak");

        var second = await SyncAsync(false, "*.bak", "*.tmp");

        Assert.Equal(1, second.SkippedDirectories);
    }

    [Fact]
    public async Task Owner_IsFetchedOnlyForNewOrChangedEntries_InFastMode()
    {
        Write("sub/a.txt");
        await SyncAsync();
        Assert.Equal(2, _scanner.OwnerCalls);   // sub, a.txt

        _scanner.OwnerCalls = 0;
        await SyncAsync();
        Assert.Equal(0, _scanner.OwnerCalls);

        Write("sub/b.txt");
        _scanner.OwnerCalls = 0;
        await SyncAsync();
        Assert.Equal(2, _scanner.OwnerCalls);   // 新規の b.txt と、時刻が変わった sub

        _scanner.OwnerCalls = 0;
        await SyncAsync(full: true);
        Assert.Equal(3, _scanner.OwnerCalls);   // フルは全件
    }

    [Fact]
    public async Task OwnerChange_IsOnlyReflectedByFullMode()
    {
        Write("sub/a.txt");
        _scanner.OwnerName = "alice";
        await SyncAsync();

        _scanner.OwnerName = "bob";
        var fast = await SyncAsync();
        Assert.Equal(0, fast.Updated);
        Assert.Equal("alice", (await _db.Rows().SingleAsync(f => f.Name == "a.txt", TestContext.Current.CancellationToken)).Owner);

        var full = await SyncAsync(full: true);
        Assert.Equal(2, full.Updated);   // sub と a.txt
        Assert.Equal("bob", (await _db.Rows().SingleAsync(f => f.Name == "a.txt", TestContext.Current.CancellationToken)).Owner);
    }

    [Fact]
    public async Task State_IsRecorded_AndFullScanTimestampOnlyMovesOnFullScan()
    {
        Write("a.txt");
        await SyncAsync();
        var first = await _db.IndexFolderStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(first.SettingsHash);

        await SyncAsync();
        var afterFast = await _db.IndexFolderStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first.LastFullScanUtc, afterFast.LastFullScanUtc);
        Assert.True(afterFast.LastScanUtc >= first.LastScanUtc);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await SyncAsync(full: true);
        var afterFull = await _db.IndexFolderStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(afterFull.LastFullScanUtc > first.LastFullScanUtc);
    }

    [Fact]
    public async Task SyncAll_RemovesStateOfDeletedFolders_AndReportsPerFolderMode()
    {
        Write("a.txt");
        var synchronizer = new IndexSynchronizer(_scanner);
        var modes = new List<bool>();

        await synchronizer.SyncAllAsync(_db, [Target()], false, (_, _, fullScan) => modes.Add(fullScan), TestContext.Current.CancellationToken);
        await synchronizer.SyncAllAsync(_db, [Target()], false, (_, _, fullScan) => modes.Add(fullScan), TestContext.Current.CancellationToken);
        await synchronizer.SyncAllAsync(_db, [Target()], true, (_, _, fullScan) => modes.Add(fullScan), TestContext.Current.CancellationToken);

        Assert.Equal([true, false, true], modes);   // 初回はフル、2 回目は高速、--full はフル

        await synchronizer.SyncAllAsync(_db, [], ct: TestContext.Current.CancellationToken);
        Assert.Empty(await _db.IndexFolderStates.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingRoot_DoesNotRecordState()
    {
        var missing = new IndexTarget(1, Path.Combine(_root, "nope"), true, []);

        await new IndexSynchronizer(_scanner).SyncAsync(_db, missing, ct: TestContext.Current.CancellationToken);

        Assert.Empty(await _db.IndexFolderStates.ToListAsync(TestContext.Current.CancellationToken));
    }

    private sealed class CountingScanner : FileScanner
    {
        public int OwnerCalls { get; set; }

        public string OwnerName { get; set; } = "owner";

        public override string? GetOwner(string fullPath, bool isDirectory)
        {
            OwnerCalls++;
            return OwnerName;
        }
    }
}
