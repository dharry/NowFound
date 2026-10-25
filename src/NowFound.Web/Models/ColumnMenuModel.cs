namespace NowFound.Web.Models;

/// <summary>一覧の「表示する列」メニュー。Columns は表示/非表示を切り替えられる列（キーは表の data-col と同じ）。</summary>
public sealed record ColumnMenuModel(string TableId, IReadOnlyList<(string Key, string Label)> Columns);
