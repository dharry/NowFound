using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NowFound.Web.Security;

namespace NowFound.UnitTests;

public class ForcePasswordChangeMiddlewareTests
{
    private static DefaultHttpContext CreateContext(string path, bool authenticated = true, bool mustChange = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        if (authenticated)
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, "test@example.com") };
            if (mustChange)
            {
                claims.Add(new(AppUserClaimsPrincipalFactory.MustChangePasswordClaim, "true"));
            }
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }

        return context;
    }

    [Theory]
    [InlineData("/lib/bootstrap/dist/js/bootstrap.bundle.min.js")]
    [InlineData("/lib/jquery/jquery.min.js")]
    [InlineData("/css/site.css")]
    [InlineData("/js/site.js")]
    [InlineData("/favicon.ico")]
    [InlineData("/Account/ChangePassword")]
    [InlineData("/Account/Logout")]
    public async Task InvokeAsync_AllowedPaths_CallsNext(string path)
    {
        var nextCalled = false;
        var middleware = new ForcePasswordChangeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(path, authenticated: true, mustChange: true);
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(302, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Home/Index")]
    [InlineData("/Settings")]
    [InlineData("/Logs")]
    [InlineData("/Users")]
    public async Task InvokeAsync_ProtectedPaths_WhenMustChangePassword_RedirectsToChangePassword(string path)
    {
        var nextCalled = false;
        var middleware = new ForcePasswordChangeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(path, authenticated: true, mustChange: true);
        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(302, context.Response.StatusCode);
        Assert.Equal("/Account/ChangePassword", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task InvokeAsync_WhenPasswordChangeNotRequired_CallsNext()
    {
        var nextCalled = false;
        var middleware = new ForcePasswordChangeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext("/Settings", authenticated: true, mustChange: false);
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(302, context.Response.StatusCode);
    }
}
