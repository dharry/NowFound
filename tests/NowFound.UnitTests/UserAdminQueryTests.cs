using Microsoft.Extensions.DependencyInjection;
using NowFound.Application;
using NowFound.Application.Users;
using NowFound.Domain;

namespace NowFound.UnitTests;

public class UserAdminQueryTests
{
    private static async Task<(TestHost Host, IServiceScope Scope, IUserAdminService Service)> CreateServiceAsync()
    {
        var host = await TestHost.CreateAsync();
        var scope = host.Services.CreateScope();
        return (host, scope, scope.ServiceProvider.GetRequiredService<IUserAdminService>());
    }

    [Fact]
    public async Task ListPageAsync_RoleFilterAndSort_WorksWithSubqueriesWithoutLoadingAllAdmins()
    {
        var (host, scope, service) = await CreateServiceAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;

        // 初期状態で admin@nowfound.invalid がシードされている。
        // 追加で管理者1名、一般ユーザー2名を作成
        await service.CreateAsync(new CreateUserRequest("admin2@example.com", "Passw0rd!", IsAdmin: true, RequirePasswordChange: false));
        await service.CreateAsync(new CreateUserRequest("user1@example.com", "Passw0rd!", IsAdmin: false, RequirePasswordChange: false));
        await service.CreateAsync(new CreateUserRequest("user2@example.com", "Passw0rd!", IsAdmin: false, RequirePasswordChange: false));

        // 1. "admin" ロール絞り込み
        var adminPage = await service.ListPageAsync(1, 10, new UserListQuery(Role: "admin"), ct);
        Assert.Equal(2, adminPage.TotalCount);
        Assert.All(adminPage.Items, u => Assert.True(u.IsAdmin));

        // 2. "user" ロール絞り込み
        var userPage = await service.ListPageAsync(1, 10, new UserListQuery(Role: "user"), ct);
        Assert.Equal(2, userPage.TotalCount);
        Assert.All(userPage.Items, u => Assert.False(u.IsAdmin));

        // 3. ロールによるソート（昇順: false < true なので一般ユーザーが先）
        var sortedByRoleAsc = await service.ListPageAsync(1, 10, new UserListQuery(Sort: UserSortKeys.Role, Descending: false), ct);
        Assert.Equal(4, sortedByRoleAsc.TotalCount);
        Assert.False(sortedByRoleAsc.Items[0].IsAdmin);
        Assert.True(sortedByRoleAsc.Items[^1].IsAdmin);

        // 4. ロールによるソート（降順: 管理者が先）
        var sortedByRoleDesc = await service.ListPageAsync(1, 10, new UserListQuery(Sort: UserSortKeys.Role, Descending: true), ct);
        Assert.True(sortedByRoleDesc.Items[0].IsAdmin);
        Assert.False(sortedByRoleDesc.Items[^1].IsAdmin);
    }
}
