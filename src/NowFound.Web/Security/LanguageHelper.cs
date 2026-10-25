using Microsoft.AspNetCore.Localization;
using NowFound.Domain;

namespace NowFound.Web.Security;

/// <summary>
/// 画面の表示言語の決め方。優先順は ① ログイン中ユーザーの設定（クレーム）→ ② 選んだ言語の Cookie
/// → ③ ブラウザの Accept-Language → ④ 既定（日本語）。①②は本クラスの Provider、③は標準の Provider、④は既定値。
/// 値は必ず AppLanguages で検証し、Cookie の改ざん値が HTML に出力されないようにする。
/// </summary>
public static class LanguageHelper
{
    public const string CookieName = "nowfound-lang";

    /// <summary>ログイン前の画面（ログイン画面など）でも選んだ言語で表示するため、Cookie に記憶する。</summary>
    public static void Remember(HttpContext context, string? language) =>
        context.Response.Cookies.Append(CookieName, AppLanguages.Normalize(language), new CookieOptions
        {
            Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(365),
            IsEssential = true,
        });
}

/// <summary>ログイン中ユーザーの保存済みの言語（クレーム）。</summary>
public sealed class UserLanguageRequestCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var language = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirst(AppUserClaimsPrincipalFactory.LanguageClaim)?.Value
            : null;
        return Task.FromResult(AppLanguages.IsValid(language) ? new ProviderCultureResult(language) : null);
    }
}

/// <summary>画面で選んだ言語の Cookie。</summary>
public sealed class LanguageCookieRequestCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var language = httpContext.Request.Cookies[LanguageHelper.CookieName];
        return Task.FromResult(AppLanguages.IsValid(language) ? new ProviderCultureResult(language) : null);
    }
}
