using NowFound.Application.Users;

namespace NowFound.Application.Settings;

/// <param name="IsShared">共有フォルダか。共有名は <paramref name="ShareName"/>。</param>
public sealed record FolderSummary(
    int Id, string Name, string Path, int ExcludeCount, int GroupCount, bool IsShared = false, string? ShareName = null);

public sealed class FolderEdit
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool ExcludeHidden { get; set; } = true;

    public bool IsShared { get; set; }

    /// <summary>共有名。空のときは保存時にフォルダ名（パスの末尾）を設定する。</summary>
    public string? ShareName { get; set; }

    /// <summary>除外パターン（ワイルドカード可）。</summary>
    public List<string> ExcludePatterns { get; set; } = [];
}

public sealed record GroupSummary(int Id, string Name, string? Description, int UserCount, int FolderCount, bool IsDefault = false);

public sealed class GroupEdit
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>既定のグループか。名前は変更できず、削除もできない。</summary>
    public bool IsDefault { get; set; }

    public List<string> UserIds { get; set; } = [];
    public List<int> FolderIds { get; set; } = [];
}

/// <summary>管理者向け設定（検索対象フォルダ・グループ・ページサイズ既定値）。</summary>
public interface IAdminSettingsService
{
    Task<IReadOnlyList<FolderSummary>> ListFoldersAsync(CancellationToken ct = default);

    /// <summary>検索対象フォルダの一覧用。検索・並べ替え（既定は名前順）をして 1 ページ分。</summary>
    Task<PagedResult<FolderSummary>> ListFoldersPageAsync(
        int page, int pageSize, FolderListQuery? query = null, CancellationToken ct = default);

    Task<FolderEdit?> GetFolderAsync(int id, CancellationToken ct = default);

    /// <summary>Id が 0 の場合は新規作成。</summary>
    Task<OperationResult> SaveFolderAsync(FolderEdit folder, CancellationToken ct = default);

    Task DeleteFolderAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<GroupSummary>> ListGroupsAsync(CancellationToken ct = default);

    /// <summary>グループ管理の一覧用。検索・並べ替え（既定は名前順）をして 1 ページ分。</summary>
    Task<PagedResult<GroupSummary>> ListGroupsPageAsync(
        int page, int pageSize, GroupListQuery? query = null, CancellationToken ct = default);

    Task<GroupEdit?> GetGroupAsync(int id, CancellationToken ct = default);

    /// <summary>Id が 0 の場合は新規作成。</summary>
    Task<OperationResult> SaveGroupAsync(GroupEdit group, CancellationToken ct = default);

    /// <summary>既定のグループは削除できない（失敗を返す）。</summary>
    Task<OperationResult> DeleteGroupAsync(int id, CancellationToken ct = default);

    Task<int> GetDefaultPageSizeAsync(CancellationToken ct = default);

    Task<OperationResult> SetDefaultPageSizeAsync(int pageSize, CancellationToken ct = default);

    /// <summary>操作ログ一覧のページサイズ（検索結果のページサイズとは別設定）。未設定なら 100。</summary>
    Task<int> GetLogPageSizeAsync(CancellationToken ct = default);

    Task<OperationResult> SetLogPageSizeAsync(int pageSize, CancellationToken ct = default);

    /// <summary>ユーザー管理・グループ管理・検索対象フォルダの一覧のページサイズ。未設定なら 50。</summary>
    Task<int> GetAdminListPageSizeAsync(CancellationToken ct = default);

    Task<OperationResult> SetAdminListPageSizeAsync(int pageSize, CancellationToken ct = default);

    /// <summary>インクリメンタルサーチの設定。未設定・範囲外の値は既定値になる。</summary>
    Task<IncrementalSearchSettings> GetIncrementalSearchAsync(CancellationToken ct = default);

    Task<OperationResult> SetIncrementalSearchAsync(IncrementalSearchSettings settings, CancellationToken ct = default);

    /// <summary>検索結果のエクスポートの設定。未設定・範囲外の値は既定値になる。</summary>
    Task<ExportSettings> GetExportAsync(CancellationToken ct = default);

    Task<OperationResult> SetExportAsync(ExportSettings settings, CancellationToken ct = default);

    /// <summary>フォルダを新しく追加するときに入力欄へ入れる除外パターン。管理者が未設定なら組み込みの既定値。</summary>
    Task<IReadOnlyList<string>> GetDefaultExcludePatternsAsync(CancellationToken ct = default);

    /// <summary>管理者が設定済みか（false なら組み込みの既定値）。</summary>
    Task<bool> IsDefaultExcludePatternsCustomizedAsync(CancellationToken ct = default);

    /// <summary>既定の除外パターンを保存する（空の一覧も可。その場合は「既定でも何も除外しない」）。</summary>
    Task<OperationResult> SetDefaultExcludePatternsAsync(IEnumerable<string> patterns, CancellationToken ct = default);

    /// <summary>管理者の設定を消して、組み込みの既定値に戻す。</summary>
    Task ResetDefaultExcludePatternsAsync(CancellationToken ct = default);
}

/// <summary>インクリメンタルサーチ（入力に合わせた自動検索）の設定。全ユーザー共通。</summary>
/// <param name="Enabled">使うか。</param>
/// <param name="MinChars">検索を始める入力文字数。</param>
/// <param name="DelayMs">最後の入力から検索を始めるまでの待ち時間（ミリ秒）。</param>
public sealed record IncrementalSearchSettings(bool Enabled, int MinChars, int DelayMs)
{
    public const int MinCharsMin = 1;
    public const int MinCharsMax = 20;
    public const int DelayMsMin = 100;
    public const int DelayMsMax = 5000;

    public static IncrementalSearchSettings Default { get; } = new(true, 2, 100);

    public bool IsValid =>
        MinChars is >= MinCharsMin and <= MinCharsMax && DelayMs is >= DelayMsMin and <= DelayMsMax;
}

/// <summary>検索結果のエクスポートの設定。全ユーザー共通。</summary>
/// <param name="AllowAllUsers">一般ユーザーにも許可するか（false なら管理者のみ）。</param>
/// <param name="MaxRows">書き出す最大行数。</param>
public sealed record ExportSettings(bool AllowAllUsers, int MaxRows)
{
    public const int MaxRowsMin = 1;
    public const int MaxRowsMax = 10_000_000;

    public static ExportSettings Default { get; } = new(false, 1_000_000);

    public bool IsValid => MaxRows is >= MaxRowsMin and <= MaxRowsMax;
}

public static class PageSizeLimits
{
    public const int Min = 1;
    public const int Max = 10_000;

    public static bool IsValid(int pageSize) => pageSize is >= Min and <= Max;
}
