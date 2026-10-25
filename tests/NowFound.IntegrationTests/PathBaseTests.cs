using System.Net;

namespace NowFound.IntegrationTests;

public sealed class PathBaseTests : IDisposable
{
    private readonly WebAppFactory _subdirectory = new("/nowfound");

    private readonly WebAppFactory _domain = new();

    public void Dispose()
    {
        _subdirectory.Dispose();
        _domain.Dispose();
    }

    private static string LocationPath(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).AbsolutePath;

    [Fact]
    public async Task Subdirectory_LoginPage_IsServedUnderPathBase_WithPrefixedLinks()
    {
        var client = _subdirectory.CreateBrowserLikeClient();

        var response = await client.GetAsync("/nowfound/Account/Login", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/nowfound/css/", html);
    }

    [Fact]
    public async Task Subdirectory_StaticAsset_IsServedUnderPathBase()
    {
        var client = _subdirectory.CreateBrowserLikeClient();

        var response = await client.GetAsync("/nowfound/css/themes.css", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Subdirectory_AnonymousAccess_RedirectsToLoginUnderPathBase()
    {
        var client = _subdirectory.CreateBrowserLikeClient();

        var response = await client.GetAsync("/nowfound/Users", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/nowfound/Account/Login", LocationPath(response));
    }

    [Fact]
    public async Task Subdirectory_ForcedPasswordChange_RedirectsUnderPathBase()
    {
        var client = _subdirectory.CreateBrowserLikeClient();
        var login = await client.PostFormAsync("/nowfound/Account/Login", "/nowfound/Account/Login",
            [new("Email", "admin@nowfound.invalid"), new("Password", "password123!")]);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var home = await client.GetAsync("/nowfound/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.StartsWith("/nowfound/Account/ChangePassword", LocationPath(home));
    }

    [Fact]
    public async Task DomainMode_LinksAndRedirects_HaveNoPrefix()
    {
        var client = _domain.CreateBrowserLikeClient();

        var response = await client.GetAsync("/Users", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", LocationPath(response));
    }
}
