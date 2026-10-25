namespace NowFound.Web.Security;

/// <summary>
/// HTTP セキュリティヘッダー（X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Content-Security-Policy）を付与するミドルウェア。
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string DefaultCsp = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; frame-ancestors 'self'; base-uri 'self'; form-action 'self'";

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("X-Frame-Options", "SAMEORIGIN");
            headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
            headers.TryAdd("Content-Security-Policy", DefaultCsp);

            return Task.CompletedTask;
        });

        await next(context);
    }
}
