namespace NowFound.Application;

/// <summary>1 ページ分の一覧。管理画面・ブックマークなど、サーバー側でページ分けする一覧に使う。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    /// <summary>範囲外のページ（削除で最終ページが無くなった、手入力の URL など）は、最も近いページに丸める。</summary>
    public static int ClampPage(int page, int totalCount, int pageSize)
    {
        var last = Math.Max(1, (int)Math.Ceiling(totalCount / (double)Math.Max(1, pageSize)));
        return Math.Clamp(page, 1, last);
    }
}
