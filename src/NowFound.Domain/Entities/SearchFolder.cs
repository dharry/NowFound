namespace NowFound.Domain.Entities;

/// <summary>検索対象として管理者が登録したフォルダ。</summary>
public class SearchFolder
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool ExcludeHidden { get; set; } = true;

    /// <summary>共有フォルダか。true のとき、表示・コピー時のパスを UNC（\\コンピューター名\共有名）に差し替える。</summary>
    public bool IsShared { get; set; }

    /// <summary>共有名。IsShared のときのみ使う。</summary>
    public string? ShareName { get; set; }

    public List<ExcludePattern> ExcludePatterns { get; set; } = [];
    public List<GroupSearchFolder> GroupSearchFolders { get; set; } = [];
}

/// <summary>除外ファイル・フォルダのパターン（ワイルドカード可）。</summary>
public class ExcludePattern
{
    public int Id { get; set; }
    public int SearchFolderId { get; set; }
    public string Pattern { get; set; } = string.Empty;
}

public class GroupSearchFolder
{
    public int GroupId { get; set; }
    public int SearchFolderId { get; set; }
    public Group? Group { get; set; }
    public SearchFolder? SearchFolder { get; set; }
}
