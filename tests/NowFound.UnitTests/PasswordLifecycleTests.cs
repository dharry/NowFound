using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

/// <summary>パスワードの有効期間（最終変更日時）・履歴（再利用禁止）と、アカウントの削除。</summary>
public class PasswordLifecycleTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static async Task<(string Id, UserManager<AppUser> Manager, IUserAdminService Service)> SetUpUserAsync(
        IServiceScope scope, string email = "a@example.com", string password = "Passw0rd!", bool isAdmin = false)
    {
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var created = await service.CreateAsync(new CreateUserRequest(email, password, isAdmin, false));
        Assert.True(created.Succeeded);
        var id = (await service.ListAsync()).Single(u => u.Email == email).Id;
        return (id, scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(), service);
    }

    private static Task SetPolicyAsync(IServiceScope scope, int maxAgeDays = 0, int historyCount = 0) =>
        scope.ServiceProvider.GetRequiredService<IAccountPolicyService>()
            .SetAsync(new AccountPolicy(8, false, 5, maxAgeDays, historyCount));

    // ---- 有効期間の判定 ----

    [Theory]
    [InlineData(0, 100, false)]
    [InlineData(90, 90, false)]
    [InlineData(90, 91, true)]
    [InlineData(1, 0, false)]
    [InlineData(1, 2, true)]
    public void IsExpired_ComparesTheAgeWithTheLifetime(int maxAgeDays, int ageDays, bool expected)
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, PasswordAgeRules.IsExpired(maxAgeDays, now.AddDays(-ageDays), now));
    }

    [Fact]
    public void IsExpired_UnknownChangeDate_IsNeverExpired() =>
        Assert.False(PasswordAgeRules.IsExpired(30, null, DateTime.UtcNow));

    [Fact]
    public void ExpiresAt_IsChangeDatePlusLifetime_OrNullWhenUnlimited()
    {
        var changed = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(changed.AddDays(30), PasswordAgeRules.ExpiresAt(30, changed));
        Assert.Null(PasswordAgeRules.ExpiresAt(0, changed));
        Assert.Null(PasswordAgeRules.ExpiresAt(30, null));
    }

    // ---- ポリシーの保存 ----

    [Fact]
    public async Task Policy_NewFields_AreSavedAndLoaded()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAccountPolicyService>();

        var result = await service.SetAsync(new AccountPolicy(10, true, 4, 90, 5), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        using var other = host.Services.CreateScope();
        Assert.Equal(
            new AccountPolicy(10, true, 4, 90, 5),
            await other.ServiceProvider.GetRequiredService<IAccountPolicyService>().GetAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1, 0, "Error.Policy.MaxAgeRange")]
    [InlineData(1000, 0, "Error.Policy.MaxAgeRange")]
    [InlineData(0, -1, "Error.Policy.HistoryRange")]
    [InlineData(0, 25, "Error.Policy.HistoryRange")]
    public async Task Policy_NewFields_OutOfRange_Fail(int maxAgeDays, int historyCount, string errorKey)
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAccountPolicyService>();

        var result = await service.SetAsync(new AccountPolicy(8, true, 5, maxAgeDays, historyCount), TestContext.Current.CancellationToken);

        Assert.Equal(errorKey, Assert.Single(result.Errors).Key);
        Assert.Equal(AccountPolicy.Default, host.Services.GetRequiredService<IAccountPolicyStore>().Current);
    }

    [Fact]
    public void Default_HasNoExpirationAndNoHistoryCheck()
    {
        Assert.Equal(0, AccountPolicy.Default.PasswordMaxAgeDays);
        Assert.Equal(0, AccountPolicy.Default.PasswordHistoryCount);
        Assert.True(AccountPolicy.Default.IsValid);
    }

    // ---- 最終変更日時と履歴 ----

    [Fact]
    public async Task Create_RecordsTheChangeDateAndHistory()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var (id, manager, _) = await SetUpUserAsync(scope);

        var user = (await manager.FindByIdAsync(id))!;
        Assert.NotNull(user.PasswordChangedAt);
        Assert.True(user.PasswordChangedAt >= before);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.PasswordHistories.Where(h => h.UserId == id).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeededAdmin_HasAChangeDate()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var admin = (await manager.FindByEmailAsync(DataSeeder.AdminEmail))!;

        Assert.NotNull(admin.PasswordChangedAt);
    }

    [Fact]
    public async Task ChangePassword_UpdatesTheChangeDate_AndAddsHistory()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (id, manager, service) = await SetUpUserAsync(scope);
        var first = (await manager.FindByIdAsync(id))!.PasswordChangedAt;
        await Task.Delay(20, TestContext.Current.CancellationToken);

        var result = await service.ChangePasswordAsync(id, "Passw0rd!", "Passw0rd!2");

        Assert.True(result.Succeeded);
        Assert.True((await manager.FindByIdAsync(id))!.PasswordChangedAt > first);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.PasswordHistories.CountAsync(h => h.UserId == id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdatingOtherFields_DoesNotTouchTheChangeDateOrHistory()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (id, manager, service) = await SetUpUserAsync(scope);
        var first = (await manager.FindByIdAsync(id))!.PasswordChangedAt;
        var adminId = (await service.ListAsync(TestContext.Current.CancellationToken)).Single(u => u.Email == DataSeeder.AdminEmail).Id;

        await service.SetPageSizeAsync(id, 10, TestContext.Current.CancellationToken);
        await service.UpdateAsync(new UserEdit { Id = id, IsEnabled = false }, adminId, Today, TestContext.Current.CancellationToken);

        Assert.Equal(first, (await manager.FindByIdAsync(id))!.PasswordChangedAt);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PasswordHistories.CountAsync(h => h.UserId == id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task History_KeepsOnlyTheLatest24()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (id, _, service) = await SetUpUserAsync(scope);
        var current = "Passw0rd!";

        for (var i = 1; i <= 26; i++)
        {
            var next = $"Passw0rd!{i}";
            Assert.True((await service.ChangePasswordAsync(id, current, next)).Succeeded);
            current = next;
        }

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(AccountPolicy.HistoryCountMax, await db.PasswordHistories.CountAsync(h => h.UserId == id, TestContext.Current.CancellationToken));
    }

    // ---- 再利用の禁止 ----

    [Fact]
    public async Task Reuse_IsAllowed_WhenTheHistoryCheckIsOff()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (id, _, service) = await SetUpUserAsync(scope);
        await service.ChangePasswordAsync(id, "Passw0rd!", "Passw0rd!2");

        var result = await service.ChangePasswordAsync(id, "Passw0rd!2", "Passw0rd!");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Reuse_OfTheCurrentPassword_IsRejected_WhenHistoryCountIsOne()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (id, _, service) = await SetUpUserAsync(scope);
        await SetPolicyAsync(scope, historyCount: 1);

        var result = await service.ChangePasswordAsync(id, "Passw0rd!", "Passw0rd!");

        Assert.False(result.Succeeded);
        Assert.Contains("recently", Assert.Single(result.Errors).Args[0]!.ToString());
    }

    [Fact]
    public async Task Reuse_OfAnOlderPassword_IsAllowedOnceItFallsOutOfTheWindow()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (id, _, service) = await SetUpUserAsync(scope);
        await SetPolicyAsync(scope, historyCount: 2);
        await service.ChangePasswordAsync(id, "Passw0rd!", "Passw0rd!2");

        // 直近 2 件は「Passw0rd!2」と「Passw0rd!」なので、どちらにも戻せない。
        var backToFirst = await service.ChangePasswordAsync(id, "Passw0rd!2", "Passw0rd!");
        await service.ChangePasswordAsync(id, "Passw0rd!2", "Passw0rd!3");
        // 直近 2 件が「Passw0rd!3」と「Passw0rd!2」になり、最初のパスワードは対象外になる。
        var firstAgain = await service.ChangePasswordAsync(id, "Passw0rd!3", "Passw0rd!");

        Assert.False(backToFirst.Succeeded);
        Assert.True(firstAgain.Succeeded);
    }

    [Fact]
    public async Task Reuse_ComparesTheCurrentPassword_EvenWithoutHistoryRows()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (id, _, service) = await SetUpUserAsync(scope);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.PasswordHistories.Where(h => h.UserId == id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await SetPolicyAsync(scope, historyCount: 3);

        var result = await service.ChangePasswordAsync(id, "Passw0rd!", "Passw0rd!");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Reuse_OtherUsersPasswords_AreNotAffected()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (_, _, service) = await SetUpUserAsync(scope, "a@example.com", "Shared-Passw0rd!");
        await SetPolicyAsync(scope, historyCount: 5);

        var result = await service.CreateAsync(new CreateUserRequest("b@example.com", "Shared-Passw0rd!", false, false));

        Assert.True(result.Succeeded);
    }

    // ---- 削除 ----

    private static async Task<string> AdminIdAsync(IUserAdminService service) =>
        (await service.ListAsync()).Single(u => u.Email == DataSeeder.AdminEmail).Id;

    [Fact]
    public async Task Delete_RemovesTheUserAndTheirData()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (id, manager, service) = await SetUpUserAsync(scope);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SavedSearches.Add(new SavedSearch { Name = "s", OwnerUserId = id, QueryJson = "{}" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(await db.UserGroups.Where(g => g.UserId == id).ToListAsync(TestContext.Current.CancellationToken));

        var result = await service.DeleteAsync(id, await AdminIdAsync(service), Today, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(await manager.FindByIdAsync(id));
        Assert.Empty(await db.UserGroups.Where(g => g.UserId == id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.SavedSearches.Where(s => s.OwnerUserId == id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.PasswordHistories.Where(h => h.UserId == id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(await service.ListAsync(TestContext.Current.CancellationToken), u => u.Id == id);
    }

    [Fact]
    public async Task Delete_KeepsOtherUsers()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (id, _, service) = await SetUpUserAsync(scope);
        var (otherId, _, _) = await SetUpUserAsync(scope, "b@example.com");

        await service.DeleteAsync(id, await AdminIdAsync(service), Today, TestContext.Current.CancellationToken);

        Assert.Contains(await service.ListAsync(TestContext.Current.CancellationToken), u => u.Id == otherId);
    }

    [Fact]
    public async Task Delete_UnknownUser_Fails()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        var result = await service.DeleteAsync("nope", await AdminIdAsync(service), Today, TestContext.Current.CancellationToken);

        Assert.Equal("Error.UserNotFound", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public async Task Delete_TheOnlyAdmin_IsRejected()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (id, _, service) = await SetUpUserAsync(scope);
        var adminId = await AdminIdAsync(service);

        // 管理者が 1 人だけのときは、本人でも他のユーザーでも削除できない。
        var bySelf = await service.DeleteAsync(adminId, adminId, Today, TestContext.Current.CancellationToken);
        var byOther = await service.DeleteAsync(adminId, id, Today, TestContext.Current.CancellationToken);

        Assert.Equal("Error.User.CannotDeleteLastAdmin", Assert.Single(bySelf.Errors).Key);
        Assert.Equal("Error.User.CannotDeleteLastAdmin", Assert.Single(byOther.Errors).Key);
        Assert.Contains(await service.ListAsync(TestContext.Current.CancellationToken), u => u.Id == adminId);
    }

    [Fact]
    public async Task Delete_OwnAccount_IsRejected_EvenWithAnotherAdmin()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        await SetUpUserAsync(scope, "admin2@example.com", isAdmin: true);
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var adminId = await AdminIdAsync(service);

        var result = await service.DeleteAsync(adminId, adminId, Today, TestContext.Current.CancellationToken);

        Assert.Equal("Error.User.CannotDeleteSelf", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public async Task Delete_AnAdmin_IsAllowed_WhenAnotherActiveAdminExists()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var (secondAdmin, _, service) = await SetUpUserAsync(scope, "admin2@example.com", isAdmin: true);
        var adminId = await AdminIdAsync(service);

        var result = await service.DeleteAsync(secondAdmin, adminId, Today, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join(",", result.Errors.Select(e => e.Key + ":" + string.Join("|", e.Args))));
    }

    [Fact]
    public async Task Delete_AnAdmin_IsRejected_WhenTheOtherAdminIsDisabledOrExpired()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await service.CreateAsync(new CreateUserRequest("off@example.com", "Passw0rd!", true, false, IsEnabled: false));
        await service.CreateAsync(new CreateUserRequest("old@example.com", "Passw0rd!", true, false, ExpiresOn: Today.AddDays(-1)));
        var adminId = await AdminIdAsync(service);
        var normal = await SetUpUserAsync(scope, "normal@example.com");

        // 他の管理者は無効・期限切れなので、有効な管理者は 1 人だけ。
        var result = await service.DeleteAsync(adminId, normal.Id, Today, TestContext.Current.CancellationToken);

        Assert.Equal("Error.User.CannotDeleteLastAdmin", Assert.Single(result.Errors).Key);
    }
}
