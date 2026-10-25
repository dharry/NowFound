using System.Net;

namespace NowFound.IntegrationTests;

public sealed class InformationTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Admin_SeesHostRuntimeAndDatabaseInformation_AndNavLink()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var response = await admin.GetAsync("/Information", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Environment.MachineName, html);
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, html);
        Assert.Contains("main.db", html);
        Assert.Contains("ope.db", html);
        Assert.Contains("files.db", html);
        Assert.Contains("SQLite", html);
        Assert.Contains("href=\"/Information\"", html);
    }

    [Fact]
    public async Task Anonymous_IsRedirectedToLogin()
    {
        var client = _factory.CreateBrowserLikeClient();

        var response = await client.GetAsync("/Information", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task NonAdmin_SeesOnlyComputerNameAndDotNet()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "user@example.com"), new("Password", "Passw0rd!"), new("RequirePasswordChange", "false")]);

        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "Passw0rd!");

        var response = await user.GetAsync("/Information", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/Information\"", html);
        Assert.Contains(Environment.MachineName, html);
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, html);
        Assert.DoesNotContain(System.Runtime.InteropServices.RuntimeInformation.OSDescription, html);
        Assert.DoesNotContain("main.db", html);
        Assert.DoesNotContain("files.db", html);
        Assert.Contains(Environment.Version.ToString(), html);
        Assert.DoesNotContain($"({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})", html);
    }

    [Fact]
    public async Task Footer_ShowsMachineName_AndNeverUpdated_ForSignedInUsersOnly()
    {
        var anonymous = _factory.CreateBrowserLikeClient();
        var login = await anonymous.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Powered by NowFound", login);

        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        var home = await admin.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains($"Powered by NowFound（稼働ホスト: {Environment.MachineName} / ファイル情報の更新: 未実行）", home);
    }
}
