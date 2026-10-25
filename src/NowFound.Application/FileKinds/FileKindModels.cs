namespace NowFound.Application.FileKinds;

/// <summary>画面に表示するファイルの種類。</summary>
/// <param name="Id">識別子（画面の data-kind 属性）。</param>
/// <param name="Label">アイコンにマウスを載せたときに表示する種類の名前。</param>
/// <param name="Icon">アイコン（絵文字または短い文字）。</param>
/// <param name="LabelKey">組み込みの既定の名前のままのときの翻訳キー（FileKind.&lt;key&gt;）。管理者が付けた名前なら null。</param>
public sealed record FileKind(string Id, string Label, string Icon, string? LabelKey = null);

/// <summary>種類の定義（編集・保存の単位）。</summary>
/// <param name="IsSystem">フォルダ・汎用のファイル。削除できず、拡張子を持たない（アイコンと名前だけ変えられる）。</param>
/// <param name="LabelKey">組み込みの既定の名前のままのときの翻訳キー。管理者が付けた名前なら null。</param>
public sealed record FileKindDefinition(string Key, string Label, string Icon, IReadOnlyList<string> Extensions, bool IsSystem, string? LabelKey = null);

/// <summary>管理者が画面で入力した 1 行分（検証前）。</summary>
public sealed class FileKindEdit
{
    /// <summary>既存の種類の識別子。新規は空（保存時に採番する）。</summary>
    public string? Key { get; set; }

    public string? Label { get; set; }

    public string? Icon { get; set; }

    /// <summary>拡張子の入力文字列（カンマ・空白・改行区切り）。</summary>
    public string? Extensions { get; set; }
}

public sealed record FileKindValidation(IReadOnlyList<FileKindDefinition>? Definitions, IReadOnlyList<LocalizedMessage> Errors)
{
    public bool Succeeded => Errors.Count == 0 && Definitions is not null;
}
