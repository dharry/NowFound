using Microsoft.AspNetCore.Http;
using NowFound.Domain;

namespace NowFound.Web.Security;

/// <summary>
/// ログイン画面で最後に選択された認証方式（標準 / ActiveDirectory）の記憶と復元。
/// クライアント側のタブ切り替え（Vanilla JS）からも Cookie を更新できるよう HttpOnly は false とする。
/// </summary>
public static class AuthSourceHelper
{
    public const string CookieName = "nowfound-login-auth";

    /// <summary>
    /// 次回ログイン画面アクセス時にアクティブにする認証方式を決定する。
    /// ActiveDirectory 認証が設定されていない場合は、Cookie の値にかかわらず標準を返す。
    /// </summary>
    public static string Resolve(HttpContext context, bool directoryEnabled)
    {
        if (!directoryEnabled)
        {
            return AuthSources.Local;
        }

        var cookie = context.Request.Cookies[CookieName];
        return cookie == AuthSources.ActiveDirectory
            ? AuthSources.ActiveDirectory
            : AuthSources.Local;
    }

    /// <summary>
    /// 選択された認証方式を Cookie に保存する。
    /// </summary>
    public static void Remember(HttpContext context, string? authSource)
    {
        var normalized = AuthSources.Normalize(authSource);
        context.Response.Cookies.Append(CookieName, normalized, new CookieOptions
        {
            Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(365),
            IsEssential = true,
        });
    }
}
