using Microsoft.AspNetCore.Http;
using NowFound.Domain;
using NowFound.Web.Security;

namespace NowFound.UnitTests;

public class AuthSourceHelperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(AuthSources.Local)]
    [InlineData(AuthSources.ActiveDirectory)]
    [InlineData("invalid-source")]
    public void Resolve_WhenDirectoryDisabled_AlwaysReturnsLocal(string? cookie)
    {
        var context = new DefaultHttpContext();
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{AuthSourceHelper.CookieName}={cookie}";
        }

        var result = AuthSourceHelper.Resolve(context, directoryEnabled: false);

        Assert.Equal(AuthSources.Local, result);
    }

    [Theory]
    [InlineData(null, AuthSources.Local)]
    [InlineData(AuthSources.Local, AuthSources.Local)]
    [InlineData(AuthSources.ActiveDirectory, AuthSources.ActiveDirectory)]
    [InlineData("unknown", AuthSources.Local)]
    [InlineData("evil\"><script>", AuthSources.Local)]
    public void Resolve_WhenDirectoryEnabled_ReturnsMatchingOrLocal(string? cookie, string expected)
    {
        var context = new DefaultHttpContext();
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{AuthSourceHelper.CookieName}={cookie}";
        }

        var result = AuthSourceHelper.Resolve(context, directoryEnabled: true);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Remember_SetsExpectedCookieHeaders()
    {
        var context = new DefaultHttpContext();

        AuthSourceHelper.Remember(context, AuthSources.ActiveDirectory);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{AuthSourceHelper.CookieName}={AuthSources.ActiveDirectory}", setCookie);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=", setCookie, StringComparison.OrdinalIgnoreCase);
        // クライアント側（Vanilla JS）からも書き換えるため HttpOnly は付与されないこと
        Assert.DoesNotContain("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remember_NormalizesInvalidValuesToLocal()
    {
        var context = new DefaultHttpContext();

        AuthSourceHelper.Remember(context, "invalid");

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{AuthSourceHelper.CookieName}={AuthSources.Local}", setCookie);
    }
}
