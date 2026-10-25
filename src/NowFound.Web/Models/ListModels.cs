using Microsoft.AspNetCore.Mvc;
using NowFound.Application;

namespace NowFound.Web.Models;

/// <summary>列見出しの並べ替えリンク。Active は現在その列で並んでいること。</summary>
public sealed record SortLinkModel(string Label, string? Url, bool Active, bool Descending)
{
    public string AriaSort => !Active ? "none" : Descending ? "descending" : "ascending";

    /// <summary>
    /// 列見出し 1 つ分。別の列を押したら昇順、いま並んでいる列をもう一度押したら昇順と降順を入れ替える。
    /// currentSort が null のときは既定の並び（defaultSort / defaultDescending）が有効。
    /// </summary>
    public static SortLinkModel Create(
        IUrlHelper url, string label, string key, string? currentSort, bool currentDescending,
        string defaultSort, bool defaultDescending, Func<string, bool, Dictionary<string, string?>> routeValues)
    {
        var active = (currentSort ?? defaultSort) == key;
        var descending = currentSort is null ? defaultDescending : currentDescending;
        return new SortLinkModel(label, url.Action("Index", routeValues(key, active && !descending)), active, active && descending);
    }
}

/// <summary>1 ページ分の一覧と、それを表示している検索条件。</summary>
public sealed record ListPage<TItem, TQuery>(PagedResult<TItem> Result, TQuery Query) where TQuery : ListQueryModel;

/// <summary>管理画面・ブックマークの一覧の検索条件（キーワード・並べ替え・ページ）。クエリ文字列から受け取る。</summary>
public class ListQueryModel
{
    public string? Q { get; set; }

    public string? Sort { get; set; }

    public bool Desc { get; set; }

    public int Page { get; set; } = 1;

    /// <summary>キーワード以外の絞り込み（ロールなど）。ページ移動・並べ替えのリンクに引き継ぐ。</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> ExtraRouteValues() => [];

    public Dictionary<string, string?> ToRouteValues(int page, string? sort = null, bool? desc = null)
    {
        var effectiveSort = sort ?? Sort;
        var values = new Dictionary<string, string?>
        {
            ["Q"] = string.IsNullOrWhiteSpace(Q) ? null : Q,
            ["Sort"] = string.IsNullOrEmpty(effectiveSort) ? null : effectiveSort,
            ["Desc"] = effectiveSort is not null && (desc ?? Desc) ? "true" : null,
            ["Page"] = page > 1 ? page.ToString() : null,
        };
        foreach (var (key, value) in ExtraRouteValues())
        {
            values[key] = value;
        }

        return values;
    }

    public SortLinkModel SortLink(IUrlHelper url, string label, string key, string defaultSort, bool defaultDescending = false) =>
        SortLinkModel.Create(url, label, key, Sort, Desc, defaultSort, defaultDescending, (k, d) => ToRouteValues(1, k, d));

    /// <summary>Application 層の検索条件に使うキー。空文字は未指定にする。</summary>
    public string? SortOrNull => string.IsNullOrEmpty(Sort) ? null : Sort;
}

public class UsersQueryModel : ListQueryModel
{
    /// <summary>"admin" / "user"。空は絞り込まない。</summary>
    public string? Role { get; set; }

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraRouteValues() =>
        [new("Role", string.IsNullOrEmpty(Role) ? null : Role)];
}

public class BookmarksQueryModel : ListQueryModel
{
    /// <summary>"mine"（非公開） / "public"（公開）。空は絞り込まない。</summary>
    public string? Visibility { get; set; }

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraRouteValues() =>
        [new("Visibility", string.IsNullOrEmpty(Visibility) ? null : Visibility)];
}
