using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Users;

namespace NowFound.UnitTests;

public class UserDescriptionTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static async Task<(TestHost Host, IServiceScope Scope, IUserAdminService Service)> CreateServiceAsync()
    {
        var host = await TestHost.CreateAsync();
        var scope = host.Services.CreateScope();
        return (host, scope, scope.ServiceProvider.GetRequiredService<IUserAdminService>());
    }

    [Fact]
    public async Task Create_WithoutADescription_Succeeds_AndLeavesItUnset()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;

        var result = await service.CreateAsync(new CreateUserRequest("a@example.com", "Passw0rd!", false, false));

        Assert.True(result.Succeeded);
        Assert.Null(Assert.Single(await service.ListAsync(ct), u => u.Email == "a@example.com").Description);
    }

    [Fact]
    public async Task Create_StoresTheTrimmedDescription_AndShowsItInTheList()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;

        await service.CreateAsync(new CreateUserRequest("a@example.com", "Passw0rd!", false, false, Description: "  営業部の山田  "));

        var summary = Assert.Single(await service.ListAsync(ct), u => u.Email == "a@example.com");
        Assert.Equal("営業部の山田", summary.Description);
        Assert.Equal("営業部の山田", (await service.GetAsync(summary.Id, ct))!.Description);
    }

    [Fact]
    public async Task Create_RejectsADescriptionOverTheLimit()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await service.CreateAsync(new CreateUserRequest(
            "a@example.com", "Passw0rd!", false, false, Description: new string('x', UserLimits.DescriptionMaxLength + 1)));

        Assert.False(result.Succeeded);
        Assert.Equal("Error.User.DescriptionTooLong", result.Errors.Single().Key);
    }

    [Fact]
    public async Task Create_AcceptsADescriptionOfExactlyTheLimit()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await service.CreateAsync(new CreateUserRequest(
            "a@example.com", "Passw0rd!", false, false, Description: new string('x', UserLimits.DescriptionMaxLength)));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Update_ChangesAndClearsTheDescription()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;
        await service.CreateAsync(new CreateUserRequest("a@example.com", "Passw0rd!", false, false));
        var id = Assert.Single(await service.ListAsync(ct), u => u.Email == "a@example.com").Id;
        var admin = (await service.ListAsync(ct)).First(u => u.IsAdmin).Id;

        var edit = (await service.GetAsync(id, ct))!;
        edit.Description = "経理担当";
        Assert.True((await service.UpdateAsync(edit, admin, Today, ct)).Succeeded);
        Assert.Equal("経理担当", (await service.GetAsync(id, ct))!.Description);

        edit.Description = "   ";
        Assert.True((await service.UpdateAsync(edit, admin, Today, ct)).Succeeded);
        Assert.Null((await service.GetAsync(id, ct))!.Description);
    }

    [Fact]
    public async Task Update_RejectsATooLongDescription_AndKeepsTheOldOne()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;
        await service.CreateAsync(new CreateUserRequest("a@example.com", "Passw0rd!", false, false, Description: "old"));
        var id = Assert.Single(await service.ListAsync(ct), u => u.Email == "a@example.com").Id;
        var admin = (await service.ListAsync(ct)).First(u => u.IsAdmin).Id;

        var edit = (await service.GetAsync(id, ct))!;
        edit.Description = new string('x', UserLimits.DescriptionMaxLength + 1);
        var result = await service.UpdateAsync(edit, admin, Today, ct);

        Assert.Equal("Error.User.DescriptionTooLong", result.Errors.Single().Key);
        Assert.Equal("old", (await service.GetAsync(id, ct))!.Description);
    }
}
