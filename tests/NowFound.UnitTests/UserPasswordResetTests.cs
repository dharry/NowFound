using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Users;
using NowFound.Domain;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

public class UserPasswordResetTests
{
    private static async Task<(TestHost Host, IServiceScope Scope, IUserAdminService Service, UserManager<AppUser> Manager, string AdminId)> CreateAsync()
    {
        var host = await TestHost.CreateAsync();
        var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var admin = (await service.ListAsync()).Single(u => u.IsAdmin);
        return (host, scope, service, scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(), admin.Id);
    }

    private static async Task<string> AddUserAsync(IUserAdminService service, string email = "user@example.com")
    {
        await service.CreateAsync(new CreateUserRequest(email, "OldPassw0rd!", false, false));
        return (await service.ListAsync()).Single(u => u.Email == email).Id;
    }

    [Fact]
    public async Task Reset_SetsTheNewPassword_AndOldOneStopsWorking()
    {
        var (host, scope, service, manager, adminId) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var id = await AddUserAsync(service);

        var result = await service.ResetPasswordAsync(id, "NewPassw0rd!", true, adminId, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var user = (await manager.FindByIdAsync(id))!;
        Assert.True(await manager.CheckPasswordAsync(user, "NewPassw0rd!"));
        Assert.False(await manager.CheckPasswordAsync(user, "OldPassw0rd!"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reset_AppliesTheRequireChangeFlag(bool requireChange)
    {
        var (host, scope, service, manager, adminId) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var id = await AddUserAsync(service);

        await service.ResetPasswordAsync(id, "NewPassw0rd!", requireChange, adminId, TestContext.Current.CancellationToken);

        Assert.Equal(requireChange, (await manager.FindByIdAsync(id))!.MustChangePassword);
    }

    [Fact]
    public async Task Reset_UnlocksTheUser_AndInvalidatesExistingSessions()
    {
        var (host, scope, service, manager, adminId) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var id = await AddUserAsync(service);
        var user = (await manager.FindByIdAsync(id))!;
        await manager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(15));
        await manager.AccessFailedAsync(user);
        var stampBefore = await manager.GetSecurityStampAsync(user);

        var result = await service.ResetPasswordAsync(id, "NewPassw0rd!", false, adminId, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var after = (await manager.FindByIdAsync(id))!;
        Assert.Null(after.LockoutEnd);
        Assert.Equal(0, after.AccessFailedCount);
        Assert.NotEqual(stampBefore, after.SecurityStamp);
        Assert.False((await service.GetAsync(id, TestContext.Current.CancellationToken))!.IsLockedOut);
    }

    [Fact]
    public async Task Reset_AppliesTheAccountPolicy_AndKeepsTheOldPassword()
    {
        var (host, scope, service, manager, adminId) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var id = await AddUserAsync(service);

        var result = await service.ResetPasswordAsync(id, "short", true, adminId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
        Assert.True(await manager.CheckPasswordAsync((await manager.FindByIdAsync(id))!, "OldPassw0rd!"));
    }

    [Fact]
    public async Task Reset_RecordsThePasswordChange_ForExpiryAndHistory()
    {
        var (host, scope, service, manager, adminId) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var id = await AddUserAsync(service);
        var before = (await manager.FindByIdAsync(id))!.PasswordChangedAt;
        await Task.Delay(20, TestContext.Current.CancellationToken);

        await service.ResetPasswordAsync(id, "NewPassw0rd!", false, adminId, TestContext.Current.CancellationToken);

        Assert.True((await manager.FindByIdAsync(id))!.PasswordChangedAt > before);
    }

    [Fact]
    public async Task Reset_RefusesActiveDirectoryUsers()
    {
        var (host, scope, service, _, adminId) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        await service.CreateAsync(new CreateUserRequest("taro", string.Empty, false, false, AuthSource: AuthSources.ActiveDirectory));
        var id = (await service.ListAsync(TestContext.Current.CancellationToken)).Single(u => u.Email == "taro").Id;

        var result = await service.ResetPasswordAsync(id, "NewPassw0rd!", false, adminId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("Error.User.PasswordManagedByDirectory", result.Errors.Single().Key);
    }

    [Fact]
    public async Task Reset_RefusesTheActingUser_AndUnknownUsers()
    {
        var (host, scope, service, _, adminId) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var self = await service.ResetPasswordAsync(adminId, "NewPassw0rd!", false, adminId, TestContext.Current.CancellationToken);
        var unknown = await service.ResetPasswordAsync("no-such-user", "NewPassw0rd!", false, adminId, TestContext.Current.CancellationToken);

        Assert.Equal("Error.User.CannotResetOwnPassword", self.Errors.Single().Key);
        Assert.Equal("Error.UserNotFound", unknown.Errors.Single().Key);
    }

    [Fact]
    public async Task Reset_AdminCanResetAnotherAdmin()
    {
        var (host, scope, service, manager, adminId) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        await service.CreateAsync(new CreateUserRequest("admin2@example.com", "OldPassw0rd!", true, false));
        var id = (await service.ListAsync(TestContext.Current.CancellationToken)).Single(u => u.Email == "admin2@example.com").Id;

        Assert.True((await service.ResetPasswordAsync(id, "NewPassw0rd!", false, adminId, TestContext.Current.CancellationToken)).Succeeded);
        Assert.True(await manager.CheckPasswordAsync((await manager.FindByIdAsync(id))!, "NewPassw0rd!"));
    }
}
