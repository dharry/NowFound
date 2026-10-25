using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Users;
using NowFound.Domain;

namespace NowFound.IntegrationTests;

public sealed partial class UserMailAddressFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [GeneratedRegex("<input[^>]*name=\"MailAddress\"[^>]*>")]
    private static partial Regex MailInput();

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        return admin;
    }

    private async Task<string> UserIdAsync(string account)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        return (await service.ListAsync(TestContext.Current.CancellationToken)).Single(u => u.Email == account).Id;
    }

    [Fact]
    public async Task UserList_CallsTheFirstColumnAccount_AndShowsTheMailAddress()
    {
        var admin = await AdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
        [
            new("Email", "taro"),
            new("AuthSource", AuthSources.ActiveDirectory),
            new("MailAddress", "taro@corp.example.com"),
        ]);

        var html = WebUtility.HtmlDecode(await admin.GetStringAsync("/Users", TestContext.Current.CancellationToken));

        Assert.Contains("アカウント", html);
        Assert.Contains("taro@corp.example.com", html);
        Assert.Matches("<th[^>]*data-col=\"mail\"", html);
    }

    [Fact]
    public async Task CreateForm_HasTheMailAddressField()
    {
        var admin = await AdminAsync();

        var html = await admin.GetStringAsync("/Users/Create", TestContext.Current.CancellationToken);

        Assert.Matches(MailInput(), html);
    }

    [Fact]
    public async Task Admin_CanEditTheMailAddressOfAnActiveDirectoryUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "taro"), new("AuthSource", AuthSources.ActiveDirectory)]);
        var id = await UserIdAsync("taro");

        Assert.Matches(MailInput(), await admin.GetStringAsync($"/Users/Edit?id={id}", ct));
        var response = await admin.PostFormAsync("/Users/Edit", $"/Users/Edit?id={id}",
            [new("Id", id), new("IsEnabled", "true"), new("MailAddress", "taro@corp.example.com")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var edit = await scope.ServiceProvider.GetRequiredService<IUserAdminService>().GetAsync(id, ct);
        Assert.Equal("taro@corp.example.com", edit!.MailAddress);
    }

    [Fact]
    public async Task EditForm_ForLocalUsers_ShowsTheAccountAsTheMailAddress_WithoutAnInput()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "local@example.com"), new("Password", "Passw0rd!"), new("RequirePasswordChange", "false")]);
        var id = await UserIdAsync("local@example.com");

        var page = await admin.GetStringAsync($"/Users/Edit?id={id}", ct);

        Assert.DoesNotMatch(MailInput(), page);
        Assert.Contains("標準ユーザーはアカウントがメールアドレスです", WebUtility.HtmlDecode(page));
        Assert.Contains("<p>local@example.com</p>", page);
    }

    [Fact]
    public async Task Create_WithAnInvalidMailAddress_ShowsAnError()
    {
        var admin = await AdminAsync();

        var response = await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "taro"), new("AuthSource", AuthSources.ActiveDirectory), new("MailAddress", "bad")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("メールアドレスの形式が正しくありません", html);
    }

    [Fact]
    public async Task CreateForm_RestoresTheLastAuthSource_OnlyWhenFirstOpened()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();

        var opened = await admin.GetStringAsync("/Users/Create", ct);
        Assert.Contains("data-auth-restore=\"true\"", opened);

        // 入力エラーで再表示されたときは、送信した認証方式を優先する（保存済みの値で上書きしない）。
        var response = await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "taro"), new("AuthSource", AuthSources.ActiveDirectory), new("MailAddress", "bad")]);
        var redisplayed = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("data-auth-restore=\"false\"", redisplayed);
    }
}
