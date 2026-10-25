using Microsoft.AspNetCore.Http;
using NowFound.Web.Security;

namespace NowFound.UnitTests;

public class PathBaseSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void Normalize_EmptyOrRoot_IsDomainMode(string? value) =>
        Assert.Null(PathBaseSettings.Normalize(value));

    [Theory]
    [InlineData("/nowfound", "/nowfound")]
    [InlineData("nowfound", "/nowfound")]
    [InlineData("/nowfound/", "/nowfound")]
    [InlineData("  /apps/file-search/ ", "/apps/file-search")]
    [InlineData("/app_1.v2~x", "/app_1.v2~x")]
    public void Normalize_AddsLeadingSlash_AndTrimsTrailingSlash(string value, string expected) =>
        Assert.Equal(expected, PathBaseSettings.Normalize(value));

    [Theory]
    [InlineData("/a//b")]
    [InlineData("/../x")]
    [InlineData("/.hidden")]
    [InlineData("/a b")]
    [InlineData("/a?b=1")]
    [InlineData("/a#b")]
    [InlineData("/日本語")]
    [InlineData("//evil.example.com")]
    public void Normalize_InvalidCharacters_Throws(string value) =>
        Assert.Throws<ArgumentException>(() => PathBaseSettings.Normalize(value));

    [Theory]
    [InlineData("", "/Account/Login", "/Account/Login")]
    [InlineData("/nowfound", "/Account/Login?blocked=true", "/nowfound/Account/Login?blocked=true")]
    public void Prefixed_AddsRequestPathBase(string pathBase, string appPath, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = pathBase;

        Assert.Equal(expected, PathBaseSettings.Prefixed(context.Request, appPath));
    }
}
