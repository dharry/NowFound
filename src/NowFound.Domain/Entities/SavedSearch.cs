namespace NowFound.Domain.Entities;

/// <summary>
/// 保存した検索条件（ブックマーク）。
/// 保存するのは条件だけで、結果は実行のたびに最新・実行ユーザーのアクセス権で得る。
/// </summary>
public class SavedSearch
{
    public int Id { get; set; }

    /// <summary>作成者。非公開の場合は唯一の閲覧・編集者。</summary>
    public string OwnerUserId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>true なら全ユーザーが実行できる（作成・変更・削除は管理者のみ）。</summary>
    public bool IsPublic { get; set; }

    /// <summary>一覧の先頭に固定する。</summary>
    public bool IsPinned { get; set; }

    /// <summary>検索条件（JSON）。形式はバージョン付きで、Application の SavedQueryJson が読み書きする。</summary>
    public string QueryJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
