namespace NowFound.Web.Security;

/// <summary>パスワード変更が必要なユーザーを変更画面へ誘導する。</summary>
public class ForcePasswordChangeMiddleware(RequestDelegate next)
{
    private static readonly string[] AllowedPrefixes = ["/Account/ChangePassword", "/Account/Logout"];

    public Task InvokeAsync(HttpContext context)
    {
        var mustChange = context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(AppUserClaimsPrincipalFactory.MustChangePasswordClaim, "true");

        if (mustChange && !IsAllowed(context.Request.Path))
        {
            context.Response.Redirect(PathBaseSettings.Prefixed(context.Request, "/Account/ChangePassword"));
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool IsAllowed(PathString path) =>
        AllowedPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))
        || path.StartsWithSegments("/css") || path.StartsWithSegments("/js") || path.StartsWithSegments("/lib")
        || path.Value == "/favicon.ico";
}
