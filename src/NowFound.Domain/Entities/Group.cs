namespace NowFound.Domain.Entities;

/// <summary>ユーザーグループ。検索対象フォルダへのアクセス権限の単位。</summary>
public class Group
{
    public const string DefaultName = "デフォルトグループ";

    /// <summary>既定のグループの説明（作成時に保存する）。画面では表示言語の文言に差し替える。</summary>
    public const string DefaultDescription = "新規ユーザーが自動で所属するグループ";

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>既定のグループ。新規ユーザーが自動で所属する。名前の変更・削除はできない。</summary>
    public bool IsDefault { get; set; }

    public List<UserGroup> UserGroups { get; set; } = [];
    public List<GroupSearchFolder> GroupSearchFolders { get; set; } = [];
}

public class UserGroup
{
    public string UserId { get; set; } = string.Empty;
    public int GroupId { get; set; }
    public Group? Group { get; set; }
}
