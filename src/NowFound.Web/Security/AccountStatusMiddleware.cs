using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NowFound.Application;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;
using NowFound.Web.Services;

namespace NowFound.Web.Security;

/// <summary>
/// ログイン中のユーザーが無効化・期限切れ・削除されたら、次のリクエストでサインアウトしてログイン画面へ戻す。
/// パスワードの有効期間が過ぎていたら、パスワード変更画面へ誘導する。
/// 認証済みリクエストごとのアカウント状態確認をキャッシュ（短時間TTL・無効化連動）を介して行い、DB負荷を抑える。
/// </summary>
public class AccountStatusMiddleware(RequestDelegate next)
{
    private static readonly string[] PasswordChangeAllowedPrefixes = ["/Account/ChangePassword", "/Account/Logout"];

    public async Task InvokeAsync(
        HttpContext context,
        AppDbContext db,
        SignInManager<AppUser> signInManager,
        RequestLogger requestLogger,
        IAccountPolicyStore policyStore,
        IAccountStatusCache statusCache)
    {
        var userId = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            : null;

        if (userId is not null && !IsStatic(context.Request.Path))
        {
            var state = await statusCache.GetOrLoadAsync(userId, async (id, ct) =>
            {
                var u = await db.Users.AsNoTracking()
                    .Where(x => x.Id == id)
                    .Select(x => new { x.IsEnabled, x.ExpiresOn, x.PasswordChangedAt, x.SecurityStamp })
                    .FirstOrDefaultAsync(ct);
                return u is null
                    ? new AccountStateSnapshot(false, false, null, null, null)
                    : new AccountStateSnapshot(true, u.IsEnabled, u.ExpiresOn, u.PasswordChangedAt, u.SecurityStamp);
            }, context.RequestAborted);

            // 削除されたユーザーは、無効と同じに扱う。
            var status = !state.Exists
                ? AccountStatus.Disabled
                : AccountStatusRules.Evaluate(state.IsEnabled, state.ExpiresOn, AccountClock.Today);
            if (status != AccountStatus.Active)
            {
                await requestLogger.LogAsync(
                    OperationType.AccessDenied,
                    new LocalizedMessage(status == AccountStatus.Expired ? "Log.Account.Expired" : "Log.Account.Disabled"));
                await signInManager.SignOutAsync();
                context.Response.Redirect(PathBaseSettings.Prefixed(context.Request, "/Account/Login?blocked=true"));
                return;
            }

            // ロール変更などでセキュリティスタンプが変わったら、古い Cookie のセッションは次のリクエストで無効にする。
            // Identity 標準の検証は既定 30 分間隔のため、その間は旧ロールの権限が残ってしまう。
            var cookieStamp = context.User.FindFirst(signInManager.Options.ClaimsIdentity.SecurityStampClaimType)?.Value;
            if (!string.Equals(state.SecurityStamp, cookieStamp, StringComparison.Ordinal))
            {
                await requestLogger.LogAsync(OperationType.AccessDenied, new LocalizedMessage("Log.Account.SessionRevoked"));
                await signInManager.SignOutAsync();
                context.Response.Redirect(PathBaseSettings.Prefixed(context.Request, "/Account/Login"));
                return;
            }

            if (PasswordAgeRules.IsExpired(policyStore.Current.PasswordMaxAgeDays, state.PasswordChangedAt, DateTime.UtcNow)
                && !IsPasswordChangePath(context.Request.Path))
            {
                context.Response.Redirect(PathBaseSettings.Prefixed(context.Request, "/Account/ChangePassword?expired=true"));
                return;
            }
        }

        await next(context);
    }

    private static bool IsPasswordChangePath(PathString path) =>
        PasswordChangeAllowedPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsStatic(PathString path) =>
        path.StartsWithSegments("/css") || path.StartsWithSegments("/js") || path.StartsWithSegments("/lib");
}
