using NowFound.Application.FileKinds;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain;

namespace NowFound.Web.Models;

public class FolderEditViewModel
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool ExcludeHidden { get; set; } = true;

    public bool IsShared { get; set; }

    public string? ShareName { get; set; }

    /// <summary>1 行 1 パターン。</summary>
    public string ExcludePatterns { get; set; } = string.Empty;

    public FolderEdit ToDto() => new()
    {
        Id = Id,
        Name = Name,
        Path = Path,
        ExcludeHidden = ExcludeHidden,
        IsShared = IsShared,
        ShareName = ShareName,
        ExcludePatterns = [.. ExcludePatterns.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)],
    };

    public static FolderEditViewModel FromDto(FolderEdit dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        Path = dto.Path,
        ExcludeHidden = dto.ExcludeHidden,
        IsShared = dto.IsShared,
        ShareName = dto.ShareName,
        ExcludePatterns = string.Join(Environment.NewLine, dto.ExcludePatterns),
    };
}

public class GroupEditViewModel
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>既定のグループ。名前は変更できない。</summary>
    public bool IsDefault { get; set; }

    public List<string> UserIds { get; set; } = [];

    public List<int> FolderIds { get; set; } = [];

    public IReadOnlyList<UserSummary> AllUsers { get; set; } = [];

    public IReadOnlyList<FolderSummary> AllFolders { get; set; } = [];

    public GroupEdit ToDto() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        UserIds = UserIds,
        FolderIds = FolderIds,
    };
}

public class PreferencesViewModel
{
    public int? PageSize { get; set; }

    /// <summary>ユーザー設定が未指定の場合に使われる管理者既定値（表示用）。</summary>
    public int DefaultPageSize { get; set; }

    public string Theme { get; set; } = AppThemes.Default;

    /// <summary>表示言語（AppLanguages）。null は変更しない。</summary>
    public string? Language { get; set; }

    /// <summary>ブックマーク一覧のページサイズ。空欄は既定値。</summary>
    public int? BookmarkPageSize { get; set; }
}

public class PageSizeViewModel
{
    public int? PageSize { get; set; }

    /// <summary>操作ログ一覧のページサイズ。</summary>
    public int? LogPageSize { get; set; }

    /// <summary>ユーザー管理・グループ管理・検索対象フォルダの一覧のページサイズ。</summary>
    public int? AdminListPageSize { get; set; }

    /// <summary>インクリメンタルサーチを使うか。</summary>
    public bool IncrementalEnabled { get; set; }

    /// <summary>インクリメンタルサーチを始める入力文字数。</summary>
    public int? IncrementalMinChars { get; set; }

    /// <summary>インクリメンタルサーチの待ち時間（ミリ秒）。</summary>
    public int? IncrementalDelayMs { get; set; }

    /// <summary>検索結果のエクスポートを一般ユーザーにも許可するか（false なら管理者のみ）。</summary>
    public bool ExportAllowAllUsers { get; set; }

    /// <summary>検索結果のエクスポートで書き出す最大行数。</summary>
    public int? ExportMaxRows { get; set; }

    /// <summary>ユーザー設定が未指定の場合に使われる管理者既定値（表示用）。</summary>
    public int DefaultPageSize { get; set; }
}

/// <summary>ファイルの種類の編集 1 行分。</summary>
public class FileKindRowViewModel
{
    /// <summary>既存の種類の識別子。新しく追加した行は空。</summary>
    public string? Key { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    /// <summary>カンマ区切りの拡張子（入力時は空白・改行でも可）。</summary>
    public string Extensions { get; set; } = string.Empty;

    /// <summary>フォルダと汎用のファイル。削除できず、拡張子は指定できない。</summary>
    public bool IsSystem => Key is DefaultFileKinds.FolderKey or DefaultFileKinds.GenericKey;
}

public class FileKindsEditViewModel
{
    public List<FileKindRowViewModel>? Rows { get; set; } = [];

    /// <summary>管理者が編集済みか（false なら組み込みの既定値）。</summary>
    public bool IsCustomized { get; set; }
}

/// <summary>新規フォルダの既定の除外パターンの編集。</summary>
public class ExcludeDefaultsViewModel
{
    /// <summary>1 行 1 パターン。</summary>
    public string Patterns { get; set; } = string.Empty;

    /// <summary>管理者が編集済みか（false なら組み込みの既定値）。</summary>
    public bool IsCustomized { get; set; }
}
