using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Users;
using NowFound.Domain;

namespace NowFound.IntegrationTests;

/// <summary>管理者によるパスワードの再設定（標準ユーザーのみ）。</summary>
public sealed class UserPasswordResetFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        return admin;
    }

    private async Task<string> AddUserAsync(string email, string authSource = AuthSources.Local)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await service.CreateAsync(new CreateUserRequest(email, authSource == AuthSources.Local ? "OldPassw0rd!" : string.Empty, false, false, AuthSource: authSource));
        return (await service.ListAsync(TestContext.Current.CancellationToken)).Single(u => u.Email == email).Id;
    }

    private Task<HttpResponseMessage> ResetAsync(HttpClient admin, string id, string password, bool requireChange) =>
        admin.PostFormAsync("/Users/ResetPassword", "/Account/Preferences",
            requireChange
                ? [new("id", id), new("newPassword", password), new("requireChange", "true")]
                : [new("id", id), new("newPassword", password)]);

    [Fact]
    public async Task EditPage_ShowsTheResetForm_ForLocalUsers_AndNotForDirectoryUsers_OrSelf()
    {
        var admin = await AdminAsync();
        var local = await AddUserAsync("user@example.com");
        var directory = await AddUserAsync("taro", AuthSources.ActiveDirectory);
        var adminId = await UserIdAsync("admin@nowfound.invalid");
        var ct = TestContext.Current.CancellationToken;

        var localHtml = await admin.GetStringAsync($"/Users/Edit?id={local}", ct);
        Assert.Contains("action=\"/Users/ResetPassword\"", localHtml);
        Assert.Contains("name=\"newPassword\"", localHtml);
        Assert.Contains("name=\"requireChange\"", localHtml);

        Assert.DoesNotContain("/Users/ResetPassword", await admin.GetStringAsync($"/Users/Edit?id={directory}", ct));
        Assert.DoesNotContain("/Users/ResetPassword", await admin.GetStringAsync($"/Users/Edit?id={adminId}", ct));
    }

    private async Task<string> UserIdAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserAdminService>().ListAsync(TestContext.Current.CancellationToken))
            .Single(u => u.Email == email).Id;
    }

    [Fact]
    public async Task Admin_ResetsThePassword_AndTheUserCanSignInWithTheNewOne()
    {
        var admin = await AdminAsync();
        var id = await AddUserAsync("user@example.com");

        var response = await ResetAsync(admin, id, "NewPassw0rd!", requireChange: false);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "NewPassw0rd!");
        var home = await user.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);

        // 古いパスワードではもうログインできない（ログイン画面が再表示される）。
        var oldClient = _factory.CreateBrowserLikeClient();
        var oldLogin = await oldClient.PostFormAsync("/Account/Login", "/Account/Login",
            [new("Email", "user@example.com"), new("Password", "OldPassw0rd!")]);
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
    }

    [Fact]
    public async Task RequireChange_SendsTheUserToThePasswordChangePage()
    {
        var admin = await AdminAsync();
        var id = await AddUserAsync("user@example.com");
        await ResetAsync(admin, id, "NewPassw0rd!", requireChange: true);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "NewPassw0rd!");

        var response = await user.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("ChangePassword", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task WeakPassword_IsRefused_WithAnErrorShownOnTheEditPage()
    {
        var admin = await AdminAsync();
        var id = await AddUserAsync("user@example.com");

        await ResetAsync(admin, id, "short", requireChange: false);

        var html = await admin.GetStringAsync($"/Users/Edit?id={id}", TestContext.Current.CancellationToken);
        Assert.Contains("role=\"alert\"", html);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "OldPassw0rd!");
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task DirectoryUsers_AreRefused()
    {
        var admin = await AdminAsync();
        var id = await AddUserAsync("taro", AuthSources.ActiveDirectory);

        await ResetAsync(admin, id, "NewPassw0rd!", requireChange: false);

        var html = await admin.GetStringAsync($"/Users/Edit?id={id}", TestContext.Current.CancellationToken);
        Assert.Contains("role=\"alert\"", html);
    }

    [Fact]
    public async Task NonAdmins_CannotReset()
    {
        var target = await AddUserAsync("target@example.com");
        await AddUserAsync("other@example.com");
        var other = _factory.CreateBrowserLikeClient();
        await other.LoginAsync("other@example.com", "OldPassw0rd!");

        var response = await ResetAsync(other, target, "NewPassw0rd!", requireChange: false);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        var check = _factory.CreateBrowserLikeClient();
        await check.LoginAsync("target@example.com", "OldPassw0rd!");
        Assert.Equal(HttpStatusCode.OK, (await check.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task UnknownUser_Returns404()
    {
        var admin = await AdminAsync();

        var response = await ResetAsync(admin, "no-such-user", "NewPassw0rd!", requireChange: false);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
