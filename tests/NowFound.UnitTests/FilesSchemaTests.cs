using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NowFound.Application.Indexing;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Indexing;

namespace NowFound.UnitTests;

/// <summary>files.db の正規化スキーマ（フルパス廃止、Directories / Owners 分離、FTS の分割）の振る舞い。</summary>
public sealed class FilesSchemaTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nowfound-schema-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nowfound-schemadb-{Guid.NewGuid():N}.db");
    private readonly OwnerScanner _scanner = new();
    private FilesDbContext _db = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _db = NewContext();
        await _db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        Directory.Delete(_root, recursive: true);
        SqliteTestCleanup.DeleteDatabase(_dbPath);
    }

    private FilesDbContext NewContext() => new(new DbContextOptionsBuilder<FilesDbContext>()
        .UseSqlite($"Data Source={_dbPath}")
        .AddInterceptors(new SqlitePragmaInterceptor())
        .Options);

    private void Write(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private IndexTarget Target() => new(1, _root, true, []);

    private Task<SyncResult> SyncAsync(bool full = false) =>
        new IndexSynchronizer(_scanner, batchSize: 2, racyWindow: TimeSpan.Zero).SyncAsync(_db, Target(), full);

    private async Task<List<string>> StringsAsync(string sql) =>
        await _db.Database.SqlQueryRaw<string>(sql).ToListAsync();

    private async Task<int> CountAsync(string sql) =>
        (await _db.Database.SqlQueryRaw<int>(sql).ToListAsync()).Single();

    [Fact]
    public async Task FilesTable_HasNoFullPathFolderPathOrOwnerColumns()
    {
        var columns = await StringsAsync("SELECT name AS Value FROM pragma_table_info('Files')");

        Assert.DoesNotContain("FullPath", columns);
        Assert.DoesNotContain("FolderPath", columns);
        Assert.DoesNotContain("Owner", columns);
        Assert.Contains("DirectoryId", columns);
        Assert.Contains("OwnerId", columns);
    }

    [Fact]
    public async Task FolderPath_IsStoredOncePerDirectory_NotOncePerFile()
    {
        Write("sub/a.txt");
        Write("sub/b.txt");
        Write("sub/c.txt");

        await SyncAsync();

        // フォルダは「ルート」と「sub」の 2 つ。ファイルは sub 自身を含めて 4 件。
        var paths = await _db.Directories.Select(d => d.Path).OrderBy(p => p).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([_root, Path.Combine(_root, "sub")], paths);
        Assert.Equal(4, await _db.Files.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await _db.Files.CountAsync(f => f.Directory!.Path == Path.Combine(_root, "sub"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OwnerNames_AreStoredOnce_AndSharedByFiles()
    {
        Write("a.txt");
        Write("b.txt");
        _scanner.OwnerName = "BUILTIN\\Administrators";

        await SyncAsync();

        Assert.Equal(["BUILTIN\\Administrators"], await _db.Owners.Select(o => o.Name).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _db.Files.Select(f => f.OwnerId).Distinct().CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OwnerChange_AddsANewOwnerRow_AndRepointsTheFile()
    {
        Write("a.txt");
        _scanner.OwnerName = "alice";
        await SyncAsync();

        _scanner.OwnerName = "bob";
        await SyncAsync(full: true);

        Assert.Equal(["alice", "bob"], await _db.Owners.Select(o => o.Name).OrderBy(n => n).ToListAsync(TestContext.Current.CancellationToken));
        Assert.All(await _db.Rows().ToListAsync(TestContext.Current.CancellationToken), row => Assert.Equal("bob", row.Owner));
    }

    [Fact]
    public async Task UnreadableOwner_IsStoredAsNull_WithoutCreatingAnOwnerRow()
    {
        Write("a.txt");
        _scanner.OwnerName = null;

        await SyncAsync();

        Assert.Empty(await _db.Owners.ToListAsync(TestContext.Current.CancellationToken));
        Assert.All(await _db.Files.ToListAsync(TestContext.Current.CancellationToken), f => Assert.Null(f.OwnerId));
    }

    [Fact]
    public async Task EmptiedDirectories_AreRemoved_TogetherWithTheirFtsEntries()
    {
        Write("gone/x.txt");
        Write("keep/y.txt");
        await SyncAsync();
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) AS Value FROM DirectoriesFts WHERE DirectoriesFts MATCH '\"gone\"'"));

        Directory.Delete(Path.Combine(_root, "gone"), recursive: true);
        await SyncAsync();

        var paths = await _db.Directories.Select(d => d.Path).ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(paths, p => p.EndsWith("gone"));
        Assert.Contains(paths, p => p.EndsWith("keep"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) AS Value FROM DirectoriesFts WHERE DirectoriesFts MATCH '\"gone\"'"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) AS Value FROM FilesFts WHERE FilesFts MATCH '\"x.txt\"'"));
    }

    [Fact]
    public async Task FilesFts_IndexesNamesOnly_AndDirectoriesFts_IndexesFolderPaths()
    {
        Write("sub/doc.txt");
        await SyncAsync();

        // ルート自身は Files にエントリを持たない。そのフォルダ名は、フォルダ側の FTS にだけ現れる。
        var rootName = Path.GetFileName(_root);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) AS Value FROM FilesFts WHERE FilesFts MATCH '\"doc.txt\"'"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) AS Value FROM FilesFts WHERE FilesFts MATCH '\"{rootName}\"'"));
        Assert.True(await CountAsync($"SELECT COUNT(*) AS Value FROM DirectoriesFts WHERE DirectoriesFts MATCH '\"{rootName}\"'") >= 1);
    }

    [Fact]
    public async Task UpdatingMetadata_KeepsFtsIntact_AndRenamingReindexes()
    {
        Write("report.txt", "1");
        await SyncAsync();
        File.WriteAllText(Path.Combine(_root, "report.txt"), "much longer content");
        await SyncAsync(full: true);

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) AS Value FROM FilesFts WHERE FilesFts MATCH '\"report\"'"));

        await _db.Database.ExecuteSqlRawAsync("UPDATE Files SET Name = 'renamed.txt' WHERE Name = 'report.txt'", TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) AS Value FROM FilesFts WHERE FilesFts MATCH '\"report\"'"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) AS Value FROM FilesFts WHERE FilesFts MATCH '\"renamed\"'"));
    }

    [Fact]
    public async Task UpdatingDirectoryPath_ReindexesDirectoriesFts()
    {
        Write("original_dir/doc.txt", "1");
        await SyncAsync();

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) AS Value FROM DirectoriesFts WHERE DirectoriesFts MATCH '\"original_dir\"'"));

        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE Directories SET Path = REPLACE(Path, 'original_dir', 'renamed_dir')",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) AS Value FROM DirectoriesFts WHERE DirectoriesFts MATCH '\"original_dir\"'"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) AS Value FROM DirectoriesFts WHERE DirectoriesFts MATCH '\"renamed_dir\"'"));
    }

    [Fact]
    public async Task SameNameInSameDirectory_IsRejected_ButAllowedInDifferentDirectories()
    {
        var first = new DirectoryEntry { SearchFolderId = 1, Path = "C:\\a" };
        var second = new DirectoryEntry { SearchFolderId = 1, Path = "C:\\b" };
        _db.Directories.AddRange(first, second);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        FileEntry Entry(int directoryId) => new()
        {
            SearchFolderId = 1, DirectoryId = directoryId, Name = "same.txt", Extension = "txt",
            ModifiedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
        };

        _db.Files.AddRange(Entry(first.Id), Entry(second.Id));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        _db.Files.Add(Entry(first.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DirectoryWithFiles_CannotBeDeleted_ForeignKeyIsEnforced()
    {
        Write("a.txt");
        await SyncAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => _db.Directories.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SyncAll_WithNoTargets_RemovesFilesDirectoriesAndFtsEntries()
    {
        Write("sub/a.txt");
        var synchronizer = new IndexSynchronizer(_scanner, racyWindow: TimeSpan.Zero);
        await synchronizer.SyncAllAsync(_db, [Target()], ct: TestContext.Current.CancellationToken);

        await synchronizer.SyncAllAsync(_db, [], ct: TestContext.Current.CancellationToken);

        Assert.Empty(await _db.Files.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await _db.Directories.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) AS Value FROM DirectoriesFts WHERE DirectoriesFts MATCH '\"sub\"'"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) AS Value FROM FilesFts WHERE FilesFts MATCH '\"a.txt\"'"));
    }

    [Fact]
    public async Task Migration_FromThePreviousSchema_RecreatesTablesAndForcesFullScan()
    {
        var migrator = _db.GetService<IMigrator>();

        // 変更前のスキーマ（フルパス列あり）に戻し、旧形式のデータと「前回実行済み」の状態を入れる。
        await migrator.MigrateAsync("AddIndexFolderState", TestContext.Current.CancellationToken);
        Assert.Contains("FullPath", await StringsAsync("SELECT name AS Value FROM pragma_table_info('Files')"));
        await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Files(SearchFolderId,Name,FolderPath,FullPath,IsDirectory,Size,Extension,Owner,ModifiedAt,CreatedAt) " +
            "VALUES (1,'old.txt','C:\\x','C:\\x\\old.txt',0,1,'txt','alice','2026-01-01 00:00:00','2026-01-01 00:00:00')", TestContext.Current.CancellationToken);
        await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO IndexFolderStates(SearchFolderId,SettingsHash,LastScanUtc,LastFullScanUtc) " +
            "VALUES (1,'h','2026-01-01 00:00:00','2026-01-01 00:00:00')", TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        // 新スキーマ。旧データは持ち越さず、状態も消える（次回の Indexer が必ずフル走査になる）。
        var columns = await StringsAsync("SELECT name AS Value FROM pragma_table_info('Files')");
        Assert.DoesNotContain("FullPath", columns);
        Assert.Contains("DirectoryId", columns);
        Assert.Equal(0, await _db.Files.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await _db.IndexFolderStates.CountAsync(TestContext.Current.CancellationToken));
        var objects = await StringsAsync("SELECT name AS Value FROM sqlite_master WHERE type IN ('table','trigger')");
        Assert.Contains("FilesFts", objects);
        Assert.Contains("DirectoriesFts", objects);
        Assert.Contains("Files_au", objects);
        Assert.Contains("Directories_ad", objects);

        // 再収集できる。
        Write("a.txt");
        var result = await SyncAsync();
        Assert.Equal(1, result.Added);
    }

    [Fact]
    public async Task Migration_Down_RestoresThePreviousSchema()
    {
        var migrator = _db.GetService<IMigrator>();

        await migrator.MigrateAsync("AddIndexFolderState", TestContext.Current.CancellationToken);

        var objects = await StringsAsync("SELECT name AS Value FROM sqlite_master WHERE type IN ('table','trigger')");
        Assert.DoesNotContain("Directories", objects);
        Assert.DoesNotContain("Owners", objects);
        Assert.DoesNotContain("DirectoriesFts", objects);
        Assert.Contains("FilesFts", objects);
        Assert.Contains("FullPath", await StringsAsync("SELECT name AS Value FROM pragma_table_info('Files')"));
    }

    private sealed class OwnerScanner : FileScanner
    {
        public string? OwnerName { get; set; } = "owner";

        public override string? GetOwner(string fullPath, bool isDirectory) => OwnerName;
    }
}
