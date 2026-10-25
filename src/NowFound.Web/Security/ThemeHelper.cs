using NowFound.Domain;

namespace NowFound.Web.Security;

/// <summary>
/// 現在のリクエストに適用する画面テーマの解決。
/// ログイン中はユーザー設定（クレーム）、未ログイン時は前回選んだテーマの Cookie、いずれもなければ既定。
/// 値は必ず AppThemes で検証し、Cookie の改ざん値が HTML に出力されないようにする。
/// </summary>
public static class ThemeHelper
{
    public const string CookieName = "nowfound-theme";

    public static string Resolve(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return AppThemes.Normalize(context.User.FindFirst(AppUserClaimsPrincipalFactory.ThemeClaim)?.Value);
        }

        return AppThemes.Normalize(context.Request.Cookies[CookieName]);
    }

    /// <summary>ログイン画面でも前回のテーマで表示するため、選択中のテーマを Cookie に記憶する。</summary>
    public static void Remember(HttpContext context, string? theme) =>
        context.Response.Cookies.Append(CookieName, AppThemes.Normalize(theme), new CookieOptions
        {
            Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(365),
            IsEssential = true,
        });
}
