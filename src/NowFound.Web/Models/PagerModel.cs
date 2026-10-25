namespace NowFound.Web.Models;

/// <summary>前へ／次へのページ移動リンク。1 ページに収まる場合は何も表示しない。</summary>
public sealed record PagerModel(
    int Page,
    int TotalPages,
    string? PreviousUrl,
    string? NextUrl,
    int PageSize = 0,
    int TotalCount = 0)
{
    public bool IsVisible => TotalPages > 1;

    public int StartItem => TotalCount <= 0 ? 0 : (Page - 1) * PageSize + 1;
    public int EndItem => TotalCount <= 0 ? 0 : Math.Min(Page * PageSize, TotalCount);

    /// <summary>ページ分けした一覧から、前へ／次へのリンクを作る。url はページ番号から URL を返す。</summary>
    public static PagerModel From<T>(NowFound.Application.PagedResult<T> result, Func<int, string?> url) => new(
        result.Page,
        result.TotalPages,
        result.Page > 1 ? url(result.Page - 1) : null,
        result.Page < result.TotalPages ? url(result.Page + 1) : null,
        result.PageSize,
        result.TotalCount);

    /// <summary>検索結果ページから、前へ／次へのリンクを作る。</summary>
    public static PagerModel From(NowFound.Application.Search.SearchPage result, Func<int, string?> url) => new(
        result.Page,
        result.TotalPages,
        result.Page > 1 ? url(result.Page - 1) : null,
        result.Page < result.TotalPages ? url(result.Page + 1) : null,
        result.PageSize,
        result.TotalCount);
}
