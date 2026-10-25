namespace NowFound.Domain.Entities;

/// <summary>
/// 検索結果に表示する「ファイルの種類」の設定（アイコン・名前・対応する拡張子）。管理者が編集する。
/// 1 行も無い間は、組み込みの既定値を使う。
/// </summary>
public class FileKindSetting
{
    public int Id { get; set; }

    /// <summary>種類の識別子（画面の data-kind 属性になる）。フォルダは "folder"、汎用のファイルは "file"。</summary>
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>アイコン（絵文字または短い文字）。</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>対応する拡張子（小文字・ドットなし・カンマ区切り）。フォルダ・汎用のファイルは空。</summary>
    public string Extensions { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
