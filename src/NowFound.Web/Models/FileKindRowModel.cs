namespace NowFound.Web.Models;

/// <summary>種類の編集行の描画用モデル。Index はテンプレート用に文字列（__INDEX__）も取る。</summary>
public sealed record FileKindRowModel(string Index, FileKindRowViewModel Row);
