using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

public class UserAccountStatusTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static async Task<string> CreateAsync(
        IUserAdminService service, string email, bool isAdmin = false, bool enabled = true, DateOnly? expiresOn = null)
    {
        var result = await service.CreateAsync(new CreateUserRequest(email, "Passw0rd!", isAdmin, false, enabled, expiresOn));
        Assert.True(result.Succeeded);
        return (await service.ListAsync()).Single(u => u.Email == email).Id;
    }

    private static async Task<string> AdminIdAsync(IUserAdminService service) =>
        (await service.ListAsync()).Single(u => u.Email == DataSeeder.AdminEmail).Id;

    [Fact]
    public async Task Create_DefaultsToEnabledWithoutExpiry()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        await CreateAsync(service, "a@example.com");

        var user = Assert.Single(await service.ListAsync(TestContext.Current.CancellationToken), u => u.Email == "a@example.com");
        Assert.True(user.IsEnabled);
        Assert.Null(user.ExpiresOn);
        Assert.False(user.IsLockedOut);
    }

    [Fact]
    public async Task Create_WithDisabledAndExpiry_StoresThem()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        var id = await CreateAsync(service, "a@example.com", enabled: false, expiresOn: Today);

        var edit = await service.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(edit);
        Assert.False(edit.IsEnabled);
        Assert.Equal(Today, edit.ExpiresOn);
    }

    [Fact]
    public async Task Get_UnknownUser_ReturnsNull()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<IUserAdminService>().GetAsync("nope", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_DisableAndSetExpiry_IsStored()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var id = await CreateAsync(service, "a@example.com");
        var adminId = await AdminIdAsync(service);

        var result = await service.UpdateAsync(
            new UserEdit { Id = id, IsEnabled = false, ExpiresOn = Today.AddDays(30) }, adminId, Today, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var edit = (await service.GetAsync(id, TestContext.Current.CancellationToken))!;
        Assert.False(edit.IsEnabled);
        Assert.Equal(Today.AddDays(30), edit.ExpiresOn);
    }

    [Fact]
    public async Task Update_ClearingTheExpiry_MakesItUnlimited()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var id = await CreateAsync(service, "a@example.com", expiresOn: Today);
        var adminId = await AdminIdAsync(service);

        await service.UpdateAsync(new UserEdit { Id = id, IsEnabled = true, ExpiresOn = null }, adminId, Today, TestContext.Current.CancellationToken);

        Assert.Null((await service.GetAsync(id, TestContext.Current.CancellationToken))!.ExpiresOn);
    }

    [Fact]
    public async Task Update_UnknownUser_Fails()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        var result = await service.UpdateAsync(new UserEdit { Id = "nope" }, "x", Today, TestContext.Current.CancellationToken);

        Assert.Equal("Error.UserNotFound", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public async Task Update_PromoteAndDemote_SwapsTheRole()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var id = await CreateAsync(service, "a@example.com");
        var adminId = await AdminIdAsync(service);

        await service.UpdateAsync(new UserEdit { Id = id, IsAdmin = true, IsEnabled = true }, adminId, Today, TestContext.Current.CancellationToken);
        var user = (await manager.FindByIdAsync(id))!;
        var promotedRoles = await manager.GetRolesAsync(user);
        await service.UpdateAsync(new UserEdit { Id = id, IsAdmin = false, IsEnabled = true }, adminId, Today, TestContext.Current.CancellationToken);
        var demotedRoles = await manager.GetRolesAsync(user);

        Assert.Equal(["Admin"], promotedRoles);
        Assert.Equal(["User"], demotedRoles);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Update_OwnAccount_CannotBeDisabledExpiredOrDemoted(bool enabled, bool expiredYesterday, bool demote)
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await CreateAsync(service, "other@example.com", isAdmin: true);
        var adminId = await AdminIdAsync(service);

        var result = await service.UpdateAsync(
            new UserEdit { Id = adminId, IsAdmin = !demote, IsEnabled = enabled, ExpiresOn = expiredYesterday ? Today.AddDays(-1) : null },
            adminId,
            Today,
            TestContext.Current.CancellationToken);

        Assert.Equal("Error.User.CannotChangeSelf", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public async Task Update_OwnAccount_ExtendingTheExpiry_IsAllowed()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var adminId = await AdminIdAsync(service);

        var result = await service.UpdateAsync(
            new UserEdit { Id = adminId, IsAdmin = true, IsEnabled = true, ExpiresOn = Today.AddYears(1) }, adminId, Today, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Update_TheLastActiveAdmin_CannotBeDisabledOrDemoted()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var adminId = await AdminIdAsync(service);
        var otherAdmin = await CreateAsync(service, "other@example.com", isAdmin: true);

        // 他にも有効な管理者がいる間は変更できるが、最後の 1 人になると拒否される。
        var first = await service.UpdateAsync(
            new UserEdit { Id = otherAdmin, IsAdmin = true, IsEnabled = false }, adminId, Today, TestContext.Current.CancellationToken);
        var demote = await service.UpdateAsync(
            new UserEdit { Id = adminId, IsAdmin = false, IsEnabled = true }, otherAdmin, Today, TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded);
        Assert.Equal("Error.User.LastAdmin", Assert.Single(demote.Errors).Key);
    }

    [Fact]
    public async Task Update_AnExpiredAdmin_DoesNotCountAsAnActiveAdmin()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var adminId = await AdminIdAsync(service);
        var expiredAdmin = await CreateAsync(service, "old@example.com", isAdmin: true, expiresOn: Today.AddDays(-1));

        var result = await service.UpdateAsync(
            new UserEdit { Id = adminId, IsAdmin = true, IsEnabled = false }, expiredAdmin, Today, TestContext.Current.CancellationToken);

        Assert.Equal("Error.User.LastAdmin", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public async Task Unlock_ClearsTheLockout()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var id = await CreateAsync(service, "a@example.com");
        var user = (await manager.FindByIdAsync(id))!;
        for (var i = 0; i < AccountPolicy.Default.MaxFailedAttempts; i++)
        {
            await manager.AccessFailedAsync(user);
        }

        var lockedList = Assert.Single(await service.ListAsync(TestContext.Current.CancellationToken), u => u.Id == id);
        var result = await service.UnlockAsync(id, TestContext.Current.CancellationToken);

        Assert.True(lockedList.IsLockedOut, "locked flag after failures");
        Assert.True(result.Succeeded);
        Assert.False((await service.GetAsync(id, TestContext.Current.CancellationToken))!.IsLockedOut);
        Assert.Equal(0, await manager.GetAccessFailedCountAsync((await manager.FindByIdAsync(id))!));
    }

    [Fact]
    public async Task Unlock_UnknownUser_Fails()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IUserAdminService>().UnlockAsync("nope", TestContext.Current.CancellationToken);

        Assert.Equal("Error.UserNotFound", Assert.Single(result.Errors).Key);
    }
}
