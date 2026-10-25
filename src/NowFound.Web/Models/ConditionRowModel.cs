using NowFound.Application.Search;

namespace NowFound.Web.Models;

/// <summary>検索条件 1 行分の描画用モデル。Index はテンプレート用に文字列（__INDEX__）も取る。</summary>
public sealed record ConditionRowModel(string Index, SearchCondition Condition, bool Removable);

public static class SearchLabels
{
    // ID（列挙値）は固定のまま、表示名は SearchResource のキーから引く。並びは画面の選択肢の順。
    public static readonly IReadOnlyDictionary<SearchField, string> FieldKeys = new Dictionary<SearchField, string>
    {
        [SearchField.All] = "Search.Field.All",
        [SearchField.FileName] = "Search.Field.FileName",
        [SearchField.FolderName] = "Search.Field.FolderName",
        [SearchField.Size] = "Search.Field.Size",
        [SearchField.Extension] = "Search.Field.Extension",
        [SearchField.Owner] = "Search.Field.Owner",
        [SearchField.ModifiedDate] = "Search.Field.ModifiedDate",
        [SearchField.CreatedDate] = "Search.Field.CreatedDate",
    };

    /// <summary>サイズの比較演算子の表示名のキー（記号と読み）。</summary>
    public static readonly IReadOnlyDictionary<SizeComparison, string> SizeComparisonKeys = new Dictionary<SizeComparison, string>
    {
        [SizeComparison.GreaterOrEqual] = "Search.Comparison.GreaterOrEqual",
        [SizeComparison.GreaterThan] = "Search.Comparison.GreaterThan",
        [SizeComparison.LessOrEqual] = "Search.Comparison.LessOrEqual",
        [SizeComparison.LessThan] = "Search.Comparison.LessThan",
        [SizeComparison.Equal] = "Search.Comparison.Equal",
        [SizeComparison.NotEqual] = "Search.Comparison.NotEqual",
    };

    /// <summary>ログ用の比較演算子の記号。</summary>
    public static string Symbol(SizeComparison comparison) => comparison switch
    {
        SizeComparison.GreaterThan => ">",
        SizeComparison.LessOrEqual => "<=",
        SizeComparison.LessThan => "<",
        SizeComparison.Equal => "=",
        SizeComparison.NotEqual => "!=",
        _ => ">=",
    };
}
