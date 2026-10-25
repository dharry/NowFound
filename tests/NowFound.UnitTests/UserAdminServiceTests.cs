using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

public class UserAdminServiceTests
{
    [Fact]
    public async Task Create_NormalUser_AppearsInListWithUserRole()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        var result = await service.CreateAsync(new CreateUserRequest("a@example.com", "Passw0rd!", false, true));
        var list = await service.ListAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var created = Assert.Single(list, u => u.Email == "a@example.com");
        Assert.False(created.IsAdmin);
        Assert.True(created.MustChangePassword);
    }

    [Fact]
    public async Task Create_AdminWithoutForcedChange_StoresFlags()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        await service.CreateAsync(new CreateUserRequest("b@example.com", "Passw0rd!", true, false));
        var created = Assert.Single(await service.ListAsync(TestContext.Current.CancellationToken), u => u.Email == "b@example.com");

        Assert.True(created.IsAdmin);
        Assert.False(created.MustChangePassword);
    }

    [Fact]
    public async Task Update_RoleChange_RotatesSecurityStamp_ButOtherChangesDoNot()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var today = DateOnly.FromDateTime(DateTime.Now);
        await service.CreateAsync(new CreateUserRequest("stamp@example.com", "Passw0rd!", true, false));
        var user = (await userManager.FindByEmailAsync("stamp@example.com"))!;
        var original = user.SecurityStamp;

        var sameRole = await service.UpdateAsync(new UserEdit { Id = user.Id, Email = user.Email!, IsAdmin = true, IsEnabled = true }, "someone-else", today, TestContext.Current.CancellationToken);
        var afterSameRole = (await userManager.FindByIdAsync(user.Id))!.SecurityStamp;
        var demote = await service.UpdateAsync(new UserEdit { Id = user.Id, Email = user.Email!, IsAdmin = false, IsEnabled = true }, "someone-else", today, TestContext.Current.CancellationToken);
        var afterDemote = (await userManager.FindByIdAsync(user.Id))!.SecurityStamp;

        Assert.True(sameRole.Succeeded);
        Assert.Equal(original, afterSameRole);
        Assert.True(demote.Succeeded);
        Assert.NotEqual(original, afterDemote);
    }

    private static async Task RemoveUserRoleAsync(TestHost host)
    {
        using var scope = host.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var role = (await roleManager.FindByNameAsync(NowFound.Domain.AppRoles.User))!;
        Assert.True((await roleManager.DeleteAsync(role)).Succeeded);
    }

    [Fact]
    public async Task Create_WhenRoleAssignmentFails_RollsBackTheUser()
    {
        await using var host = await TestHost.CreateAsync();
        await RemoveUserRoleAsync(host);   // 途中（ロール付与）で失敗させる
        using (var scope = host.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
            await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(new CreateUserRequest("half@example.com", "Passw0rd!", false, false)));
        }

        using var check = host.Services.CreateScope();
        Assert.Null(await check.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync("half@example.com"));
    }

    [Fact]
    public async Task Update_WhenRoleChangeFailsHalfway_RollsBackEveryChange()
    {
        await using var host = await TestHost.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Now);
        string id;
        using (var scope = host.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
            await service.CreateAsync(new CreateUserRequest("second@example.com", "Passw0rd!", true, false));
            id = (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync("second@example.com"))!.Id;
        }

        await RemoveUserRoleAsync(host);   // 管理者→一般への変更は、Admin を外した後の User 付与で失敗する
        using (var scope = host.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
            await Assert.ThrowsAnyAsync<Exception>(() => service.UpdateAsync(
                new UserEdit { Id = id, Email = "second@example.com", IsAdmin = false, IsEnabled = false },
                "someone-else", today, TestContext.Current.CancellationToken));
        }

        using var check = host.Services.CreateScope();
        var userManager = check.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await userManager.FindByIdAsync(id))!;
        Assert.True(user.IsEnabled);
        Assert.True(await userManager.IsInRoleAsync(user, NowFound.Domain.AppRoles.Admin));
    }

    [Fact]
    public async Task Update_ConcurrentlyDisablingTwoAdmins_NeverLeavesZeroActiveAdmins()
    {
        await using var host = await TestHost.CreateAsync();
        var today = DateOnly.FromDateTime(DateTime.Now);
        string firstId, secondId;
        using (var scope = host.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await scope.ServiceProvider.GetRequiredService<IUserAdminService>()
                .CreateAsync(new CreateUserRequest("second@example.com", "Passw0rd!", true, false));
            firstId = (await userManager.FindByEmailAsync(DataSeeder.AdminEmail))!.Id;
            secondId = (await userManager.FindByEmailAsync("second@example.com"))!.Id;
        }

        async Task<OperationResult> DisableAsync(string id, string email)
        {
            using var scope = host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IUserAdminService>().UpdateAsync(
                new UserEdit { Id = id, Email = email, IsAdmin = true, IsEnabled = false }, "someone-else", today, TestContext.Current.CancellationToken);
        }

        var results = await Task.WhenAll(DisableAsync(firstId, DataSeeder.AdminEmail), DisableAsync(secondId, "second@example.com"));

        Assert.Single(results, r => r.Succeeded);
        using var check = host.Services.CreateScope();
        var users = await check.ServiceProvider.GetRequiredService<UserManager<AppUser>>().GetUsersInRoleAsync(NowFound.Domain.AppRoles.Admin);
        Assert.Single(users, u => u.IsEnabled);
    }

    [Fact]
    public async Task Create_DuplicateEmail_Fails()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        var result = await service.CreateAsync(new CreateUserRequest(DataSeeder.AdminEmail, "Passw0rd!", false, true));

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
        Assert.All(result.Errors, e => Assert.Equal(LocalizedMessage.RawKey, e.Key));   // Identity のエラーは翻訳済みの文としてそのまま表示する
    }

    [Fact]
    public async Task Create_WeakPassword_Fails()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        var result = await service.CreateAsync(new CreateUserRequest("c@example.com", "short", false, true));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ChangePassword_ValidCurrent_ClearsMustChangeFlag()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admin = (await users.FindByEmailAsync(DataSeeder.AdminEmail))!;

        var result = await service.ChangePasswordAsync(admin.Id, DataSeeder.AdminPassword, "NewPassw0rd!");

        Assert.True(result.Succeeded);
        var reloaded = (await users.FindByIdAsync(admin.Id))!;
        Assert.False(reloaded.MustChangePassword);
        Assert.True(await users.CheckPasswordAsync(reloaded, "NewPassw0rd!"));
    }

    [Fact]
    public async Task ChangePassword_WrongCurrent_FailsAndKeepsFlag()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admin = (await users.FindByEmailAsync(DataSeeder.AdminEmail))!;

        var result = await service.ChangePasswordAsync(admin.Id, "wrong", "NewPassw0rd!");

        Assert.False(result.Succeeded);
        Assert.True((await users.FindByIdAsync(admin.Id))!.MustChangePassword);
    }

    [Fact]
    public async Task ChangePassword_UnknownUser_Fails()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        var result = await service.ChangePasswordAsync("missing", "x", "NewPassw0rd!");

        Assert.False(result.Succeeded);
    }
}
