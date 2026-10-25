using Microsoft.Extensions.DependencyInjection;
using NowFound.Application;
using NowFound.Application.Users;
using NowFound.Domain;

namespace NowFound.UnitTests;

public class UserMailAddressTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);

    private static async Task<(TestHost Host, IServiceScope Scope, IUserAdminService Service)> CreateServiceAsync()
    {
        var host = await TestHost.CreateAsync();
        var scope = host.Services.CreateScope();
        return (host, scope, scope.ServiceProvider.GetRequiredService<IUserAdminService>());
    }

    private static CreateUserRequest DirectoryUser(string name, string? mail = null) =>
        new(name, string.Empty, false, false, AuthSource: AuthSources.ActiveDirectory, MailAddress: mail);

    [Fact]
    public async Task Create_LocalUser_UsesTheAccountAsTheMailAddress_IgnoringTheInput()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;

        var result = await service.CreateAsync(new CreateUserRequest(
            "local@example.com", "Passw0rd!", false, false, MailAddress: "other@example.com"));

        Assert.True(result.Succeeded);
        var summary = Assert.Single(await service.ListAsync(ct), u => u.Email == "local@example.com");
        Assert.Equal("local@example.com", summary.MailAddress);
        Assert.Equal("local@example.com", (await service.GetAsync(summary.Id, ct))!.MailAddress);
    }

    [Fact]
    public async Task Create_DirectoryUser_StoresTheOptionalMailAddress()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;

        await service.CreateAsync(DirectoryUser("taro", " taro@corp.example.com "));
        await service.CreateAsync(DirectoryUser("jiro"));

        var list = await service.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("taro@corp.example.com", list.Single(u => u.Email == "taro").MailAddress);
        Assert.Null(list.Single(u => u.Email == "jiro").MailAddress);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b@c")]
    public async Task Create_DirectoryUser_RejectsAnInvalidMailAddress(string mail)
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await service.CreateAsync(DirectoryUser("taro", mail));

        Assert.False(result.Succeeded);
        Assert.Equal("Error.User.MailAddressInvalid", result.Errors.Single().Key);
        Assert.DoesNotContain(await service.ListAsync(TestContext.Current.CancellationToken), u => u.Email == "taro");
    }

    [Fact]
    public async Task Update_DirectoryUser_ChangesAndClearsTheMailAddress()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;
        await service.CreateAsync(DirectoryUser("taro"));
        var id = (await service.ListAsync(ct)).Single(u => u.Email == "taro").Id;

        var edit = (await service.GetAsync(id, ct))!;
        edit.MailAddress = "taro@corp.example.com";
        Assert.True((await service.UpdateAsync(edit, "someone-else", Today, ct)).Succeeded);
        Assert.Equal("taro@corp.example.com", (await service.GetAsync(id, ct))!.MailAddress);

        edit.MailAddress = "  ";
        Assert.True((await service.UpdateAsync(edit, "someone-else", Today, ct)).Succeeded);
        Assert.Null((await service.GetAsync(id, ct))!.MailAddress);

        edit.MailAddress = "bad";
        var invalid = await service.UpdateAsync(edit, "someone-else", Today, ct);
        Assert.Equal("Error.User.MailAddressInvalid", invalid.Errors.Single().Key);
    }

    [Fact]
    public async Task Update_LocalUser_KeepsTheMailAddressEqualToTheAccount()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;
        await service.CreateAsync(new CreateUserRequest("local@example.com", "Passw0rd!", false, false));
        var id = (await service.ListAsync(ct)).Single(u => u.Email == "local@example.com").Id;

        var edit = (await service.GetAsync(id, ct))!;
        edit.MailAddress = "changed@example.com";
        Assert.True((await service.UpdateAsync(edit, "someone-else", Today, ct)).Succeeded);

        Assert.Equal("local@example.com", (await service.GetAsync(id, ct))!.MailAddress);
    }

    [Fact]
    public async Task Search_MatchesEitherTheAccountOrTheMailAddress()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;
        await service.CreateAsync(DirectoryUser("taro", "taro.yamada@corp.example.com"));
        await service.CreateAsync(DirectoryUser("jiro", "j_contact@corp.example.com"));
        await service.CreateAsync(DirectoryUser("saburo"));
        await service.CreateAsync(new CreateUserRequest("local@example.com", "Passw0rd!", false, false));

        async Task<string[]> Find(string keyword) =>
            [.. (await service.ListPageAsync(1, 10, new UserListQuery(keyword), ct)).Items.Select(u => u.Email).Order()];

        Assert.Equal(["taro"], await Find("TARO"));              // アカウントに一致
        Assert.Equal(["taro"], await Find("yamada"));            // メールアドレスにだけ一致
        Assert.Equal(["jiro", "taro"], await Find("corp.example")); // 複数のメールアドレスに一致
        Assert.Equal(["jiro"], await Find("j_contact"));         // _ は文字として扱う
        Assert.Empty(await Find("j-contact"));
        Assert.Equal(["local@example.com"], await Find("local@"));
        Assert.Equal(["saburo"], await Find("saburo"));          // メールアドレス未設定でもアカウントで見つかる
    }

    [Fact]
    public async Task SeededAdmin_WithoutAStoredMailAddress_FallsBackToTheAccount()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;

        var admin = Assert.Single(await service.ListAsync(TestContext.Current.CancellationToken), u => u.IsAdmin);

        Assert.Equal(admin.Email, admin.MailAddress);
    }
}
