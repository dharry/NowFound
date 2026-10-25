using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NowFound.Application.Indexing;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Indexing;

/// <summary>
/// main.db の定義に基づいて走査し、files.db を差分更新する。
/// 既定は高速モード（前回と更新日時が同じディレクトリは読み直さず、オーナーは変更分だけ取得）。
/// フルモードは全件を走査し、オーナーの変更も検出する。
/// </summary>
public class IndexSynchronizer(FileScanner scanner, int batchSize = 5000, TimeSpan? racyWindow = null, ILogger<IndexSynchronizer>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>
    /// 前回の走査開始より、この時間以内に更新されたディレクトリは「変化なし」と見なさず読み直す。
    /// ディレクトリの更新日時は時計の刻み（十数ミリ秒〜）でしか進まないため、走査の直後に起きた変更が
    /// 同じ時刻に見えて検出できなくなることがある（git の racy 問題と同じ）。
    /// </summary>
    private readonly TimeSpan _racyWindow = racyWindow ?? TimeSpan.FromSeconds(2);

    /// <summary>main.db から収集対象の定義を読み込む。</summary>
    public static async Task<IReadOnlyList<IndexTarget>> LoadTargetsAsync(AppDbContext mainDb, CancellationToken ct = default) =>
        await mainDb.SearchFolders
            .AsNoTracking()
            .Select(f => new IndexTarget(
                f.Id,
                f.Path,
                f.ExcludeHidden,
                f.ExcludePatterns.Select(p => p.Pattern).ToList()))
            .ToListAsync(ct);

    /// <param name="full">true なら全フォルダをフル走査する。</param>
    /// <param name="onFolderCompleted">フォルダごとの完了通知（対象, 結果, フル走査だったか）。</param>
    public async Task<SyncResult> SyncAllAsync(
        FilesDbContext filesDb,
        IReadOnlyList<IndexTarget> targets,
        bool full = false,
        Action<IndexTarget, SyncResult, bool>? onFolderCompleted = null,
        CancellationToken ct = default)
    {
        var result = SyncResult.Empty;

        // 定義から外れた（削除された）フォルダの情報を除去する。ファイルを先に消してからフォルダを消す。
        var activeIds = targets.Select(t => t.SearchFolderId).ToList();
        var removedFolders = await filesDb.Files
            .Where(f => !activeIds.Contains(f.SearchFolderId))
            .ExecuteDeleteAsync(ct);
        await filesDb.Directories
            .Where(d => !activeIds.Contains(d.SearchFolderId))
            .ExecuteDeleteAsync(ct);
        await filesDb.IndexFolderStates
            .Where(s => !activeIds.Contains(s.SearchFolderId))
            .ExecuteDeleteAsync(ct);
        result += new SyncResult(0, 0, removedFolders, 0);

        foreach (var target in targets)
        {
            var (folderResult, fullScan) = await SyncCoreAsync(filesDb, target, full, ct);
            onFolderCompleted?.Invoke(target, folderResult, fullScan);
            result += folderResult;
        }

        return result;
    }

    public async Task<SyncResult> SyncAsync(
        FilesDbContext filesDb, IndexTarget target, bool full = false, CancellationToken ct = default) =>
        (await SyncCoreAsync(filesDb, target, full, ct)).Result;

    private async Task<(SyncResult Result, bool FullScan)> SyncCoreAsync(
        FilesDbContext filesDb, IndexTarget target, bool full, CancellationToken ct)
    {
        var folderId = target.SearchFolderId;

        // ルートが存在しない場合（ドライブ未接続など）は、誤って全削除しないよう何もしない。
        if (!Directory.Exists(target.Path))
        {
            var existingCount = await filesDb.Files.CountAsync(f => f.SearchFolderId == folderId, ct);
            return (new SyncResult(0, 0, 0, existingCount), full);
        }

        // フォルダパスとオーナー名は別表に 1 回だけ保存している。
        var directoryIds = await filesDb.Directories.AsNoTracking()
            .Where(d => d.SearchFolderId == folderId)
            .ToDictionaryAsync(d => d.Path, d => d.Id, StringComparer.Ordinal, ct);
        var owners = await OwnerCache.LoadAsync(filesDb, ct);

        // 前回と収集設定が変わった場合（除外パターンの変更など）は、枝刈りすると古い結果が残るためフル走査にする。
        var settingsHash = ComputeSettingsHash(target);
        var state = await filesDb.IndexFolderStates.AsNoTracking()
            .SingleOrDefaultAsync(s => s.SearchFolderId == folderId, ct);
        var fullScan = full || state is null || state.SettingsHash != settingsHash;

        var scanStartedAt = DateTime.UtcNow;

        // 【メモリ最適化】全ファイルではなく、ディレクトリ（IsDirectory == true）のみをロードして枝刈りキャッシュを構築
        var cache = fullScan
            ? null
            : await LoadDirectoryScanCacheAsync(filesDb, folderId, directoryIds, state!.LastScanUtc - _racyWindow, ct);

        int added = 0, updated = 0, unchanged = 0, removed = 0;
        var failedPaths = new HashSet<string>(StringComparer.Ordinal);
        var failedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var visitedDirectoryIds = new HashSet<int>();
        var folderOpenedDirectoryIds = new HashSet<int>();

        // ルートディレクトリが存在するため、ルートの DirectoryId は生存として記録
        if (!directoryIds.TryGetValue(target.Path, out var rootDirId))
        {
            var rootDirEntry = new DirectoryEntry { SearchFolderId = folderId, Path = target.Path };
            filesDb.Directories.Add(rootDirEntry);
            await filesDb.SaveChangesAsync(ct);
            rootDirId = rootDirEntry.Id;
            directoryIds[target.Path] = rootDirId;
            filesDb.ChangeTracker.Clear();
        }

        visitedDirectoryIds.Add(rootDirId);

        var adds = new List<ScannedEntry>(batchSize);
        var updates = new List<(long Id, ScannedEntry Entry)>();
        var deletes = new List<long>();

        // ルートディレクトリを初期フォルダとして開く
        string? currentFolderPath = target.Path;
        folderOpenedDirectoryIds.Add(rootDirId);
        Dictionary<string, ExistingFileEntry>? currentFolderFiles = await LoadFolderFilesAsync(filesDb, rootDirId, owners, ct);

        // フォルダ切り替え時の確定処理: 今回走査されなかった（削除された）直下ファイルを特定してフラッシュ
        async Task CloseCurrentFolderAsync()
        {
            if (currentFolderPath is null || currentFolderFiles is null)
            {
                return;
            }

            foreach (var (name, oldEntry) in currentFolderFiles)
            {
                var fullPath = FilePaths.Combine(currentFolderPath, name);
                if (!failedPaths.Contains(fullPath))
                {
                    deletes.Add(oldEntry.Id);
                    removed++;
                }
            }

            if (deletes.Count >= batchSize)
            {
                await FlushDeletesAsync(filesDb, deletes, ct);
            }

            currentFolderFiles = null;
        }

        foreach (var scanned in scanner.Scan(target, cache, (path, ex) =>
        {
            failedPaths.Add(path);
            if (Directory.Exists(path) || directoryIds.ContainsKey(path))
            {
                failedDirectories.Add(path);
            }
        }))
        {
            ct.ThrowIfCancellationRequested();

            // サブディレクトリ自体がスキャンされた場合、その DirectoryId も生存としてマーク
            if (scanned.IsDirectory)
            {
                if (!directoryIds.TryGetValue(scanned.FullPath, out var subDirId))
                {
                    var dirEntry = new DirectoryEntry { SearchFolderId = folderId, Path = scanned.FullPath };
                    filesDb.Directories.Add(dirEntry);
                    await filesDb.SaveChangesAsync(ct);
                    subDirId = dirEntry.Id;
                    directoryIds[scanned.FullPath] = subDirId;
                    filesDb.ChangeTracker.Clear();
                }

                visitedDirectoryIds.Add(subDirId);
            }

            // フォルダが変わった場合、前のフォルダの直下削除エントリを確定し、新フォルダの直下ファイルをロード
            if (scanned.FolderPath != currentFolderPath)
            {
                await CloseCurrentFolderAsync();

                currentFolderPath = scanned.FolderPath;
                if (!directoryIds.TryGetValue(currentFolderPath, out var dirId))
                {
                    var dirEntry = new DirectoryEntry { SearchFolderId = folderId, Path = currentFolderPath };
                    filesDb.Directories.Add(dirEntry);
                    await filesDb.SaveChangesAsync(ct);
                    dirId = dirEntry.Id;
                    directoryIds[currentFolderPath] = dirId;
                    filesDb.ChangeTracker.Clear();
                }

                visitedDirectoryIds.Add(dirId);
                folderOpenedDirectoryIds.Add(dirId);
                currentFolderFiles = await LoadFolderFilesAsync(filesDb, dirId, owners, ct);
            }

            if (currentFolderFiles is null || !currentFolderFiles.Remove(scanned.Name, out var old))
            {
                adds.Add(WithOwner(scanned));
                added++;
            }
            else
            {
                var changed = IsChanged(old, scanned);
                var entry = scanned;
                if (fullScan)
                {
                    // フルモードはオーナーも取得して比較する（オーナーのみの変更も反映）。
                    entry = WithOwner(scanned);
                    changed |= old.Owner != entry.Owner;
                }
                else if (changed)
                {
                    entry = WithOwner(scanned);
                }

                if (changed)
                {
                    updates.Add((old.Id, entry));
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }

            if (adds.Count >= batchSize)
            {
                await FlushAddsAsync(filesDb, folderId, adds, directoryIds, owners, ct);
            }

            if (updates.Count >= batchSize)
            {
                await FlushUpdatesAsync(filesDb, updates, owners, ct);
            }
        }

        // 走査完了: 最後のフォルダを確定
        await CloseCurrentFolderAsync();

        // 未フラッシュのバッチを一括反映
        await FlushAddsAsync(filesDb, folderId, adds, directoryIds, owners, ct);
        await FlushUpdatesAsync(filesDb, updates, owners, ct);
        await FlushDeletesAsync(filesDb, deletes, ct);

        // 読み直しを省いたディレクトリ直下、および読み取りエラーのディレクトリ配下の DirectoryId を保護対象に追加
        var skipped = cache?.SkippedDirectories ?? [];
        foreach (var (path, id) in directoryIds)
        {
            if (skipped.Contains(path)
                || failedDirectories.Contains(path)
                || failedDirectories.Any(d => path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                visitedDirectoryIds.Add(id);
                folderOpenedDirectoryIds.Add(id);
            }
        }

        // 生存しているが中身が空だったディレクトリ直下に残っている古いファイルを TEMP TABLE で一括削除
        var emptyVisitedDirIds = visitedDirectoryIds.Except(folderOpenedDirectoryIds).ToList();
        if (emptyVisitedDirIds.Count > 0)
        {
            var cleanedEmptyFiles = await DeleteFilesByDirectoryIdsAsync(filesDb, folderId, emptyVisitedDirIds, ct);
            removed += cleanedEmptyFiles;
        }

        // スキャンで訪問されなかった孤立ディレクトリ（フォルダごと削除されたツリー）配下のファイルを TEMP TABLE で一括削除
        var deletedDirIds = directoryIds.Values.Except(visitedDirectoryIds).ToList();
        if (deletedDirIds.Count > 0)
        {
            var orphanedFileCount = await DeleteOrphanedDirectoriesAsync(filesDb, folderId, deletedDirIds, ct);
            removed += orphanedFileCount;
        }

        if (removed > 0)
        {
            // ファイルが 1 件もなくなったフォルダの行を消す（ファイルを先に消してから）。
            await filesDb.Directories
                .Where(d => d.SearchFolderId == folderId && !filesDb.Files.Any(f => f.DirectoryId == d.Id))
                .ExecuteDeleteAsync(ct);
        }

        // 走査中に読み取りエラーが1件でも発生した場合は、次回走査で再試行させるため状態キャッシュ（LastScanUtc）を更新しない。
        if (failedPaths.Count == 0 && failedDirectories.Count == 0)
        {
            await SaveStateAsync(filesDb, folderId, settingsHash, fullScan, state, scanStartedAt, ct);
        }
        else
        {
            _logger.LogWarning(
                "検索対象フォルダ {FolderId} ({Path}) の走査中に {Count} 件の読み取りエラーが発生したため、状態キャッシュの更新を抑制しました。",
                folderId, target.Path, failedPaths.Count + failedDirectories.Count);
        }

        return (new SyncResult(added, updated, removed, unchanged, skipped.Count), fullScan);
    }

    private static async Task<DirectoryScanCache> LoadDirectoryScanCacheAsync(
        FilesDbContext db, int folderId, Dictionary<string, int> directoryIds, DateTime trustedBeforeUtc, CancellationToken ct)
    {
        var pathById = directoryIds.ToDictionary(kv => kv.Value, kv => kv.Key);

        // ディレクトリ（IsDirectory == true）のみを読み込み、メモリ消費を数MBに抑制する
        var dirRows = await db.Files.AsNoTracking()
            .Where(f => f.SearchFolderId == folderId && f.IsDirectory)
            .Select(f => new { f.DirectoryId, f.Name, f.ModifiedAt })
            .ToListAsync(ct);

        var dirEntries = new List<(string Path, DateTime ModifiedAt, string ParentPath)>(dirRows.Count);
        foreach (var r in dirRows)
        {
            if (pathById.TryGetValue(r.DirectoryId, out var parentPath))
            {
                var fullPath = FilePaths.Combine(parentPath, r.Name);
                dirEntries.Add((fullPath, r.ModifiedAt, parentPath));
            }
        }

        return new DirectoryScanCache(dirEntries, trustedBeforeUtc);
    }

    private static async Task<Dictionary<string, ExistingFileEntry>> LoadFolderFilesAsync(
        FilesDbContext db, int dirId, OwnerCache owners, CancellationToken ct)
    {
        var rows = await db.Files.AsNoTracking()
            .Where(f => f.DirectoryId == dirId)
            .Select(f => new { f.Id, f.Name, f.IsDirectory, f.Size, f.OwnerId, f.ModifiedAt, f.CreatedAt })
            .ToListAsync(ct);

        var dict = new Dictionary<string, ExistingFileEntry>(rows.Count, StringComparer.Ordinal);
        foreach (var r in rows)
        {
            dict[r.Name] = new ExistingFileEntry(
                r.Id, r.IsDirectory, r.Size, owners.NameOf(r.OwnerId), r.ModifiedAt, r.CreatedAt);
        }

        return dict;
    }

    private ScannedEntry WithOwner(ScannedEntry entry) =>
        entry with { Owner = scanner.GetOwner(entry.FullPath, entry.IsDirectory) };

    private static bool IsChanged(ExistingFileEntry old, ScannedEntry now) =>
        old.IsDirectory != now.IsDirectory || old.Size != now.Size || old.ModifiedAt != now.ModifiedAt || old.CreatedAt != now.CreatedAt;

    /// <summary>収集結果に影響する設定（パス・隠し除外・除外パターン）のハッシュ。</summary>
    internal static string ComputeSettingsHash(IndexTarget target)
    {
        var text = string.Join(
            '\n',
            [target.Path, target.ExcludeHidden.ToString(), .. target.ExcludePatterns.Order(StringComparer.Ordinal)]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <param name="scanStartedAt">LastScanUtc には終了時刻ではなく「開始時刻」を記録する（racy window の基準になるため）。</param>
    private static async Task SaveStateAsync(
        FilesDbContext db, int folderId, string hash, bool fullScan, IndexFolderState? previous, DateTime scanStartedAt, CancellationToken ct)
    {
        var now = scanStartedAt;
        var lastFull = fullScan ? now : previous?.LastFullScanUtc ?? now;

        var updatedRows = await db.IndexFolderStates.Where(s => s.SearchFolderId == folderId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.SettingsHash, hash)
                .SetProperty(s => s.LastScanUtc, now)
                .SetProperty(s => s.LastFullScanUtc, lastFull), ct);
        if (updatedRows == 0)
        {
            db.IndexFolderStates.Add(new IndexFolderState
            {
                SearchFolderId = folderId, SettingsHash = hash, LastScanUtc = now, LastFullScanUtc = lastFull,
            });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    private static async Task FlushAddsAsync(
        FilesDbContext db, int folderId, List<ScannedEntry> adds,
        Dictionary<string, int> directoryIds, OwnerCache owners, CancellationToken ct)
    {
        if (adds.Count == 0)
        {
            return;
        }

        // 参照先（フォルダ・オーナー）を先に作って ID を得てから、ファイルを挿入する。
        var newDirectories = adds.Select(a => a.FolderPath).Where(p => !directoryIds.ContainsKey(p)).Distinct().ToList();
        if (newDirectories.Count > 0)
        {
            var entities = newDirectories.Select(p => new DirectoryEntry { SearchFolderId = folderId, Path = p }).ToList();
            db.Directories.AddRange(entities);
            await db.SaveChangesAsync(ct);
            foreach (var entity in entities)
            {
                directoryIds[entity.Path] = entity.Id;
            }

            db.ChangeTracker.Clear();
        }

        await owners.EnsureAsync(db, adds.Select(a => a.Owner), ct);

        db.Files.AddRange(adds.Select(s => new FileEntry
        {
            SearchFolderId = folderId,
            DirectoryId = directoryIds[s.FolderPath],
            Name = s.Name,
            IsDirectory = s.IsDirectory,
            Size = s.Size,
            Extension = s.Extension,
            OwnerId = owners.IdOf(s.Owner),
            ModifiedAt = s.ModifiedAt,
            CreatedAt = s.CreatedAt,
        }));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        adds.Clear();
    }

    private static async Task FlushUpdatesAsync(
        FilesDbContext db, List<(long Id, ScannedEntry Entry)> updates, OwnerCache owners, CancellationToken ct)
    {
        if (updates.Count == 0)
        {
            return;
        }

        await owners.EnsureAsync(db, updates.Select(u => u.Entry.Owner), ct);

        // トランザクションとプリペアードステートメントにより、個別コミット(N+1)を解消して一括更新する
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction?)tx.GetDbTransaction();
            cmd.CommandText = @"
UPDATE Files SET
    IsDirectory = $isDir,
    Extension = $ext,
    Size = $size,
    OwnerId = $ownerId,
    ModifiedAt = $modAt,
    CreatedAt = $creAt
WHERE Id = $id;";

            var pId = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pIsDir = cmd.Parameters.Add("$isDir", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pExt = cmd.Parameters.Add("$ext", Microsoft.Data.Sqlite.SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pOwnerId = cmd.Parameters.Add("$ownerId", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pModAt = cmd.Parameters.Add("$modAt", Microsoft.Data.Sqlite.SqliteType.Text);
            var pCreAt = cmd.Parameters.Add("$creAt", Microsoft.Data.Sqlite.SqliteType.Text);

            cmd.Prepare();

            foreach (var (id, s) in updates)
            {
                var ownerId = owners.IdOf(s.Owner);
                pId.Value = id;
                pIsDir.Value = s.IsDirectory ? 1 : 0;
                pExt.Value = s.Extension;
                pSize.Value = (object?)s.Size ?? DBNull.Value;
                pOwnerId.Value = (object?)ownerId ?? DBNull.Value;
                pModAt.Value = s.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF");
                pCreAt.Value = s.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF");

                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        updates.Clear();
    }

    private static async Task FlushDeletesAsync(FilesDbContext db, List<long> deletes, CancellationToken ct)
    {
        if (deletes.Count == 0)
        {
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction?)tx.GetDbTransaction();
            cmd.CommandText = "DELETE FROM Files WHERE Id = $id;";
            var pId = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            cmd.Prepare();

            foreach (var id in deletes)
            {
                pId.Value = id;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
        deletes.Clear();
    }

    private static async Task<int> DeleteFilesByDirectoryIdsAsync(
        FilesDbContext db, int folderId, List<int> dirIds, CancellationToken ct)
    {
        if (dirIds.Count == 0)
        {
            return 0;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        var sqliteTx = (Microsoft.Data.Sqlite.SqliteTransaction?)tx.GetDbTransaction();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = sqliteTx;
            cmd.CommandText = @"
CREATE TEMP TABLE IF NOT EXISTS TempEmptyDirIds (Id INTEGER PRIMARY KEY);
DELETE FROM TempEmptyDirIds;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = sqliteTx;
            cmd.CommandText = "INSERT INTO TempEmptyDirIds (Id) VALUES ($id);";
            var pId = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            cmd.Prepare();

            foreach (var id in dirIds)
            {
                pId.Value = id;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        int deletedCount = 0;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = sqliteTx;
            cmd.CommandText = @"
DELETE FROM Files
WHERE SearchFolderId = $folderId
  AND DirectoryId IN (SELECT Id FROM TempEmptyDirIds);";
            cmd.Parameters.AddWithValue("$folderId", folderId);
            deletedCount = await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = sqliteTx;
            cmd.CommandText = "DROP TABLE IF EXISTS TempEmptyDirIds;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return deletedCount;
    }

    private static async Task<int> DeleteOrphanedDirectoriesAsync(
        FilesDbContext db, int folderId, List<int> deletedDirIds, CancellationToken ct)
    {
        if (deletedDirIds.Count == 0)
        {
            return 0;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        var sqliteTx = (Microsoft.Data.Sqlite.SqliteTransaction?)tx.GetDbTransaction();

        // 1. TEMP TABLE を作成
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = sqliteTx;
            cmd.CommandText = @"
CREATE TEMP TABLE IF NOT EXISTS TempDeletedDirIds (Id INTEGER PRIMARY KEY);
DELETE FROM TempDeletedDirIds;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 2. 削除対象の DirectoryId をバッチ挿入
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = sqliteTx;
            cmd.CommandText = "INSERT INTO TempDeletedDirIds (Id) VALUES ($id);";
            var pId = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            cmd.Prepare();

            foreach (var id in deletedDirIds)
            {
                pId.Value = id;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        // 3. 孤立ディレクトリ配下のファイルを一括削除
        int deletedFileCount = 0;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = sqliteTx;
            cmd.CommandText = @"
DELETE FROM Files
WHERE SearchFolderId = $folderId
  AND DirectoryId IN (SELECT Id FROM TempDeletedDirIds);";
            cmd.Parameters.AddWithValue("$folderId", folderId);
            deletedFileCount = await cmd.ExecuteNonQueryAsync(ct);
        }

        // 4. 孤立ディレクトリ自体を一括削除
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = sqliteTx;
            cmd.CommandText = @"
DELETE FROM Directories
WHERE SearchFolderId = $folderId
  AND Id IN (SELECT Id FROM TempDeletedDirIds);";
            cmd.Parameters.AddWithValue("$folderId", folderId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 5. TEMP TABLE を破棄
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = sqliteTx;
            cmd.CommandText = "DROP TABLE IF EXISTS TempDeletedDirIds;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return deletedFileCount;
    }

    private sealed record ExistingFileEntry(
        long Id, bool IsDirectory, long? Size, string? Owner, DateTime ModifiedAt, DateTime CreatedAt);

    /// <summary>オーナー名と ID の対応。見つからない名前は Owners 表へ追加する。</summary>
    private sealed class OwnerCache
    {
        private readonly Dictionary<string, int> _idByName;
        private readonly Dictionary<int, string> _nameById;

        private OwnerCache(Dictionary<string, int> idByName)
        {
            _idByName = idByName;
            _nameById = idByName.ToDictionary(kv => kv.Value, kv => kv.Key);
        }

        public static async Task<OwnerCache> LoadAsync(FilesDbContext db, CancellationToken ct) =>
            new(await db.Owners.AsNoTracking().ToDictionaryAsync(o => o.Name, o => o.Id, StringComparer.Ordinal, ct));

        public string? NameOf(int? id) => id is { } value && _nameById.TryGetValue(value, out var name) ? name : null;

        public int? IdOf(string? name) => name is not null && _idByName.TryGetValue(name, out var id) ? id : null;

        public async Task EnsureAsync(FilesDbContext db, IEnumerable<string?> names, CancellationToken ct)
        {
            var missing = names.OfType<string>().Where(n => !_idByName.ContainsKey(n)).Distinct().ToList();
            if (missing.Count == 0)
            {
                return;
            }

            var entities = missing.Select(n => new OwnerEntry { Name = n }).ToList();
            db.Owners.AddRange(entities);
            await db.SaveChangesAsync(ct);
            foreach (var entity in entities)
            {
                _idByName[entity.Name] = entity.Id;
                _nameById[entity.Id] = entity.Name;
            }

            db.ChangeTracker.Clear();
        }
    }

    /// <summary>前回の files.db の内容に基づく枝刈り。ディレクトリ（IsDirectory == true）のみを保持する軽量キャッシュ。</summary>
    private sealed class DirectoryScanCache : IScanCache
    {
        private readonly Dictionary<string, DateTime> _dirMtimeByPath;
        private readonly ILookup<string, string> _subdirsByParentPath;
        private readonly DateTime _trustedBeforeUtc;

        public DirectoryScanCache(
            IEnumerable<(string Path, DateTime ModifiedAt, string ParentPath)> dirEntries,
            DateTime trustedBeforeUtc)
        {
            _trustedBeforeUtc = trustedBeforeUtc;
            var list = dirEntries.ToList();
            _dirMtimeByPath = list.ToDictionary(d => d.Path, d => d.ModifiedAt, StringComparer.Ordinal);
            _subdirsByParentPath = list.ToLookup(d => d.ParentPath, d => d.Path, StringComparer.Ordinal);
        }

        public HashSet<string> SkippedDirectories { get; } = new(StringComparer.Ordinal);

        public int SkippedEntryCount => 0;

        public bool TryGetUnchanged(string directoryPath, DateTime lastWriteUtc, out IReadOnlyList<string> knownSubdirectories)
        {
            if (!_dirMtimeByPath.TryGetValue(directoryPath, out var modAt)
                || modAt != lastWriteUtc
                || modAt >= _trustedBeforeUtc)
            {
                knownSubdirectories = [];
                return false;
            }

            SkippedDirectories.Add(directoryPath);
            knownSubdirectories = [.. _subdirsByParentPath[directoryPath]];
            return true;
        }
    }
}
