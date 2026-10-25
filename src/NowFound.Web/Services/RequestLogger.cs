using System.Security.Claims;
using NowFound.Application;
using NowFound.Application.Logging;
using NowFound.Domain.Entities;

namespace NowFound.Web.Services;

/// <summary>現在のリクエストからユーザー・IP を補って操作ログを書き込む。</summary>
public class RequestLogger(IOperationLogService logService, IHttpContextAccessor accessor)
{
    /// <param name="email">未認証の操作（ログイン失敗など）で、認証情報の代わりに記録する値。</param>
    /// <param name="detail">内容。リソースのキー＋引数で渡し、表示時に管理者の言語へ翻訳する（Messages の Log.* キー）。</param>
    public Task LogAsync(OperationType operation, LocalizedMessage? detail = null, string? email = null, string? userId = null)
    {
        var context = accessor.HttpContext;
        var user = context?.User;
        var authenticated = user?.Identity?.IsAuthenticated == true;

        return logService.WriteAsync(new OperationLogEntry(
            operation,
            userId ?? (authenticated ? user!.FindFirstValue(ClaimTypes.NameIdentifier) : null),
            email ?? (authenticated ? user!.Identity!.Name ?? string.Empty : string.Empty),
            detail,
            // X-Forwarded-For は直接参照せず、UseForwardedHeaders 適用後の接続元を使う。
            context?.Connection.RemoteIpAddress?.ToString()));
    }
}
