using Microsoft.AspNetCore.Mvc;
using NowFound.Application;
using NowFound.Application.Logging;
using NowFound.Domain.Entities;

namespace NowFound.Web.Models;

public class LogSearchViewModel
{
    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public OperationType? Operation { get; set; }

    public string? User { get; set; }

    public int Page { get; set; } = 1;

    public string? Sort { get; set; }

    public bool Desc { get; set; }

    public OperationLogPage? Result { get; set; }

    /// <summary>現在の検索条件を引き継ぐ。operation / user を渡すと、その条件だけ差し替える（一覧のセルからの絞り込み用）。</summary>
    public Dictionary<string, string?> ToRouteValues(int page, OperationType? operation = null, string? user = null) => new()
    {
        ["From"] = From?.ToString("yyyy-MM-dd"),
        ["To"] = To?.ToString("yyyy-MM-dd"),
        ["Operation"] = (operation ?? Operation)?.ToString(),
        ["User"] = user ?? User,
        ["Sort"] = string.IsNullOrEmpty(Sort) ? null : Sort,
        ["Desc"] = !string.IsNullOrEmpty(Sort) && Desc ? "true" : null,
        ["Page"] = page.ToString(),
    };

    /// <summary>列見出しの並べ替えリンク（日時は既定で新しい順）。条件はそのまま、ページは 1 に戻す。</summary>
    public SortLinkModel SortLink(IUrlHelper url, string label, string key) =>
        SortLinkModel.Create(url, label, key, Sort, Desc, LogSortKeys.Time, true, (k, d) =>
        {
            var values = ToRouteValues(1);
            values["Sort"] = k;
            values["Desc"] = d ? "true" : null;
            return values;
        });
}
