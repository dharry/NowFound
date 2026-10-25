using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

public class AccountPolicyServiceTests
{
    [Fact]
    public async Task Get_WhenNothingSaved_ReturnsTheDefault()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();

        var policy = await scope.ServiceProvider.GetRequiredService<IAccountPolicyService>().GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AccountPolicy.Default, policy);
    }

    [Fact]
    public async Task Set_PersistsAndUpdatesTheStore()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAccountPolicyService>();

        var result = await service.SetAsync(new AccountPolicy(12, false, 3), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(new AccountPolicy(12, false, 3), host.Services.GetRequiredService<IAccountPolicyStore>().Current);

        // 別のスコープ（別の DbContext）から読んでも、保存した値が返る。
        using var other = host.Services.CreateScope();
        Assert.Equal(
            new AccountPolicy(12, false, 3),
            await other.ServiceProvider.GetRequiredService<IAccountPolicyService>().GetAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(4, 5, "Error.Policy.MinLengthRange")]
    [InlineData(128, 5, "Error.Policy.MinLengthRange")]
    [InlineData(8, -1, "Error.Policy.MaxFailedRange")]
    [InlineData(8, 11, "Error.Policy.MaxFailedRange")]
    public async Task Set_OutOfRange_Fails_AndKeepsTheCurrentPolicy(int minLength, int maxFailed, string errorKey)
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAccountPolicyService>();

        var result = await service.SetAsync(new AccountPolicy(minLength, true, maxFailed), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(errorKey, Assert.Single(result.Errors).Key);
        Assert.Equal(AccountPolicy.Default, host.Services.GetRequiredService<IAccountPolicyStore>().Current);
    }

    [Fact]
    public async Task Create_WithTooShortPassword_Fails_UntilThePolicyIsRelaxed()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var policy = scope.ServiceProvider.GetRequiredService<IAccountPolicyService>();
        await policy.SetAsync(new AccountPolicy(12, false, 5), TestContext.Current.CancellationToken);

        var tooShort = await users.CreateAsync(new CreateUserRequest("a@example.com", "Abcdef1!", false, false));
        await policy.SetAsync(new AccountPolicy(8, false, 5), TestContext.Current.CancellationToken);
        var ok = await users.CreateAsync(new CreateUserRequest("a@example.com", "Abcdef1!", false, false));

        Assert.False(tooShort.Succeeded);
        Assert.True(ok.Succeeded);
    }

    [Fact]
    public async Task Create_ComplexityRule_IsEnforcedOnlyWhenEnabled()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var policy = scope.ServiceProvider.GetRequiredService<IAccountPolicyService>();

        await policy.SetAsync(new AccountPolicy(8, true, 5), TestContext.Current.CancellationToken);
        var weak = await users.CreateAsync(new CreateUserRequest("a@example.com", "abcdefghij", false, false));
        await policy.SetAsync(new AccountPolicy(8, false, 5), TestContext.Current.CancellationToken);
        var allowed = await users.CreateAsync(new CreateUserRequest("a@example.com", "abcdefghij", false, false));

        Assert.False(weak.Succeeded);
        Assert.True(allowed.Succeeded);
    }

    [Fact]
    public async Task ChangePassword_UsesTheCurrentPolicy()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await users.CreateAsync(new CreateUserRequest("a@example.com", "Passw0rd!", false, false));
        var id = (await users.ListAsync(TestContext.Current.CancellationToken)).Single(u => u.Email == "a@example.com").Id;
        await scope.ServiceProvider.GetRequiredService<IAccountPolicyService>()
            .SetAsync(new AccountPolicy(16, false, 5), TestContext.Current.CancellationToken);

        var result = await users.ChangePasswordAsync(id, "Passw0rd!", "Short1!");

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(10, 10)]
    [InlineData(0, int.MaxValue)]
    public async Task IdentityOptions_FollowTheLockoutPolicy(int maxFailed, int expected)
    {
        await using var host = await TestHost.CreateAsync();
        using (var setup = host.Services.CreateScope())
        {
            await setup.ServiceProvider.GetRequiredService<IAccountPolicyService>()
                .SetAsync(new AccountPolicy(8, true, maxFailed), TestContext.Current.CancellationToken);
        }

        using var scope = host.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().Options;

        Assert.Equal(expected, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(AccountPolicy.LockoutDuration, options.Lockout.DefaultLockoutTimeSpan);
    }

    [Fact]
    public async Task Lockout_HappensAtTheConfiguredCount()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAccountPolicyService>()
            .SetAsync(new AccountPolicy(8, true, 3), TestContext.Current.CancellationToken);
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await manager.FindByEmailAsync(DataSeeder.AdminEmail))!;

        await manager.AccessFailedAsync(user);
        await manager.AccessFailedAsync(user);
        var lockedAfterTwo = await manager.IsLockedOutAsync(user);
        await manager.AccessFailedAsync(user);

        Assert.False(lockedAfterTwo);
        Assert.True(await manager.IsLockedOutAsync(user));
    }

    [Fact]
    public async Task Lockout_Zero_NeverLocks()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAccountPolicyService>()
            .SetAsync(new AccountPolicy(8, true, 0), TestContext.Current.CancellationToken);
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await manager.FindByEmailAsync(DataSeeder.AdminEmail))!;

        for (var i = 0; i < 25; i++)
        {
            await manager.AccessFailedAsync(user);
        }

        Assert.False(await manager.IsLockedOutAsync(user));
    }
}
