namespace NowFound.Application.Indexing;

/// <summary>収集対象フォルダの定義（main.db から取得）。</summary>
public sealed record IndexTarget(int SearchFolderId, string Path, bool ExcludeHidden, IReadOnlyList<string> ExcludePatterns);

/// <summary>
/// 1 件のファイル／フォルダ情報（収集結果）。
/// Owner はスキャナーでは設定しない（取得コストが高いため、必要なエントリだけ後から取得する）。
/// </summary>
public sealed record ScannedEntry(
    string Name,
    string FolderPath,
    string FullPath,
    bool IsDirectory,
    long? Size,
    string Extension,
    string? Owner,
    DateTime ModifiedAt,
    DateTime CreatedAt);

/// <param name="SkippedDirectories">前回と時刻が同じため読み直しを省いたディレクトリ数。</param>
public sealed record SyncResult(int Added, int Updated, int Removed, int Unchanged, int SkippedDirectories = 0)
{
    public static SyncResult operator +(SyncResult a, SyncResult b) =>
        new(
            a.Added + b.Added,
            a.Updated + b.Updated,
            a.Removed + b.Removed,
            a.Unchanged + b.Unchanged,
            a.SkippedDirectories + b.SkippedDirectories);

    public static SyncResult Empty { get; } = new(0, 0, 0, 0);
}
