namespace NowFound.Domain.Entities;

/// <summary>
/// files.db に保持するファイル／フォルダ情報。
/// フォルダパスとオーナー名は件数が多いほど重複するため、別表（Directories / Owners）に 1 回だけ保存して参照する。
/// フルパスは保存せず、フォルダパスと名前から組み立てる。
/// </summary>
public class FileEntry
{
    public long Id { get; set; }

    /// <summary>所属する検索対象フォルダ（アクセス権の絞り込みと一括削除のため、Directories からも辿れるが非正規化して持つ）。</summary>
    public int SearchFolderId { get; set; }

    public int DirectoryId { get; set; }
    public DirectoryEntry? Directory { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }

    /// <summary>バイト数。フォルダは null。</summary>
    public long? Size { get; set; }

    /// <summary>拡張子（ドットなし・小文字）。</summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>取得できない、または取得しない（Windows 以外など）場合は null。</summary>
    public int? OwnerId { get; set; }
    public OwnerEntry? Owner { get; set; }
    public DateTime ModifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>ファイルを直接含むフォルダ。パスはここにだけ保存する。</summary>
public class DirectoryEntry
{
    public int Id { get; set; }
    public int SearchFolderId { get; set; }
    public string Path { get; set; } = string.Empty;
}

/// <summary>ファイルのオーナー名（例: BUILTIN\Administrators）。</summary>
public class OwnerEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
