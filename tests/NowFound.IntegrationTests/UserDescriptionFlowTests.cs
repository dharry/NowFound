using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Users;

namespace NowFound.IntegrationTests;

public sealed class UserDescriptionFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

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
    public async Task CreateAndEditForms_HaveAnOptionalDescriptionField()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();

        var create = await admin.GetStringAsync("/Users/Create", ct);
        Assert.Matches("<textarea[^>]*name=\"Description\"", create);
        Assert.DoesNotMatch("<textarea[^>]*name=\"Description\"[^>]*required", create);

        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "a@example.com"), new("Password", "Passw0rd!")]);
        var id = await UserIdAsync("a@example.com");
        Assert.Matches("<textarea[^>]*name=\"Description\"", await admin.GetStringAsync($"/Users/Edit?id={id}", ct));
    }

    [Fact]
    public async Task Create_WithoutADescription_Succeeds()
    {
        var admin = await AdminAsync();

        var response = await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "a@example.com"), new("Password", "Passw0rd!")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Description_IsSavedFromTheForms_AndShownInTheList()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await AdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "a@example.com"), new("Password", "Passw0rd!"), new("Description", "営業部の山田")]);
        var id = await UserIdAsync("a@example.com");

        var list = WebUtility.HtmlDecode(await admin.GetStringAsync("/Users", ct));
        Assert.Matches("<th[^>]*data-col=\"description\"", list);
        Assert.Contains("営業部の山田", list);

        var response = await admin.PostFormAsync("/Users/Edit", $"/Users/Edit?id={id}",
            [new("Id", id), new("IsEnabled", "true"), new("Description", "経理担当")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var edit = await scope.ServiceProvider.GetRequiredService<IUserAdminService>().GetAsync(id, ct);
        Assert.Equal("経理担当", edit!.Description);
    }
}
