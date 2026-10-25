using System.Net;
using System.Text.RegularExpressions;

namespace NowFound.IntegrationTests;

public sealed class SecurityHeadersAndRateLimitTests : IDisposable
{
    private readonly WebAppFactory _factory = new(loginRateLimit: 10);

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task Response_IncludesStandardSecurityHeaders()
    {
        var client = _factory.CreateBrowserLikeClient();
        var response = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // X-Content-Type-Options: nosniff
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeOptions));
        Assert.Contains("nosniff", contentTypeOptions);

        // X-Frame-Options: SAMEORIGIN
        Assert.True(response.Headers.TryGetValues("X-Frame-Options", out var frameOptions));
        Assert.Contains("SAMEORIGIN", frameOptions);

        // Referrer-Policy: strict-origin-when-cross-origin
        Assert.True(response.Headers.TryGetValues("Referrer-Policy", out var referrerPolicy));
        Assert.Contains("strict-origin-when-cross-origin", referrerPolicy);

        // Content-Security-Policy
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var csp));
        var cspString = string.Join("; ", csp);
        Assert.Contains("default-src 'self'", cspString);
    }

    [Fact]
    public async Task LoginPost_RateLimiter_RejectsAfterExceedingLimit()
    {
        var client = _factory.CreateBrowserLikeClient();
        var ct = TestContext.Current.CancellationToken;

        // CSRF トークン取得
        var html = await client.GetStringAsync("/Account/Login", ct);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        var token = match.Groups[1].Value;

        HttpResponseMessage lastResponse = null!;

        // 10 回までは通常処理（ログイン失敗 200 OK）、11 回目で 429 TooManyRequests
        for (var i = 0; i < 11; i++)
        {
            using var content = new FormUrlEncodedContent([
                new("__RequestVerificationToken", token),
                new("Email", $"sprayer{i}@example.com"),
                new("Password", "wrong-password")
            ]);

            lastResponse = await client.PostAsync("/Account/Login", content, ct);
            if (i < 10)
            {
                Assert.NotEqual(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
    }
}
