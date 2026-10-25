using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Domain;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

public class DataSeederTests
{
    [Fact]
    public async Task Seed_CreatesAdminWithRoleAndMustChangePassword()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var admin = await users.FindByEmailAsync(DataSeeder.AdminEmail);

        Assert.NotNull(admin);
        Assert.True(admin.MustChangePassword);
        Assert.True(await users.IsInRoleAsync(admin, AppRoles.Admin));
        Assert.True(await users.CheckPasswordAsync(admin, DataSeeder.AdminPassword));
    }

    [Fact]
    public async Task Seed_IsIdempotent()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        await DataSeeder.SeedAsync(users, roles);

        Assert.Single(users.Users.Where(u => u.Email == DataSeeder.AdminEmail));
        Assert.Equal(AppRoles.All.Count, roles.Roles.Count());
    }
}
