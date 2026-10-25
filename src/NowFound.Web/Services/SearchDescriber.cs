using Microsoft.Extensions.Localization;
using NowFound.Application.Search;
using NowFound.Web.Resources;

namespace NowFound.Web.Services;

/// <summary>
/// 検索条件を操作ログ用の 1 行文字列にする。「(条件なし)」「フォルダ=」だけを現在の表示言語で出し、
/// 項目名（FileName など）と記号は言語に依らない。
/// </summary>
public static class SearchDescriber
{
    public static string Describe(
        IReadOnlyList<SearchCondition> conditions,
        SearchOperator op,
        string? folderName,
        string? folderPath,
        IStringLocalizer<SearchResource> localizer)
    {
        var parts = conditions.Where(c => !c.IsEmpty).Select(DescribeCondition).ToList();
        var text = parts.Count == 0 ? localizer["Search.Describe.NoCondition"].Value : string.Join($" {op.ToString().ToUpperInvariant()} ", parts);
        if (!string.IsNullOrEmpty(folderName))
        {
            text += $" | {localizer["Search.Describe.Folder", folderName].Value}";
            if (!string.IsNullOrEmpty(folderPath))
            {
                text += $" ({folderPath})";
            }
        }

        return text;
    }

    private static string DescribeCondition(SearchCondition c) => c.Field switch
    {
        SearchField.ModifiedDate or SearchField.CreatedDate => $"{c.Field}={c.From:yyyy-MM-dd}～{c.To:yyyy-MM-dd}",
        SearchField.Size => $"Size{Models.SearchLabels.Symbol(c.SizeComparison)}{c.SizeValue:0.####}{c.SizeUnit}",
        _ => $"{c.Field}='{c.Text?.Trim()}'",
    };
}
