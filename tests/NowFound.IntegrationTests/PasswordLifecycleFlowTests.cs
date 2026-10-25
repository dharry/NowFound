using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Infrastructure.Data;

namespace NowFound.IntegrationTests;

/// <summary>パスワードの有効期間・再利用禁止と、ユーザーの削除。</summary>
public sealed class PasswordLifecycleFlowTests : IDisposable
{
    private const string UserEmail = "user@example.com";
    private const string UserPassword = "Passw0rd!";

    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        return admin;
    }

    private static Task<HttpResponseMessage> CreateUserAsync(HttpClient admin, string email, bool isAdmin = false) =>
        admin.PostFormAsync("/Users/Create", "/Users/Create",
        [
            new("Email", email),
            new("Password", UserPassword),
            new("IsAdmin", isAdmin ? "true" : "false"),
            new("RequirePasswordChange", "false"),
            new("IsEnabled", "true"),
            new("ExpiresOn", string.Empty),
        ]);

    private static Task<HttpResponseMessage> SetPolicyAsync(HttpClient admin, int maxAgeDays, int historyCount) =>
        admin.PostFormAsync("/Settings/AccountPolicy", "/Settings/AccountPolicy",
        [
            new("PasswordMinLength", "8"),
            new("RequireComplexity", "true"),
            new("MaxFailedAttempts", "5"),
            new("PasswordMaxAgeDays", maxAgeDays.ToString()),
            new("PasswordHistoryCount", historyCount.ToString()),
        ]);

    private async Task SetPasswordAgeAsync(string email, int ageDays)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var changedAt = DateTime.UtcNow.AddDays(-ageDays);
        var user = await db.Users.Where(u => u.Email == email).SingleAsync(TestContext.Current.CancellationToken);
        user.PasswordChangedAt = changedAt;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        scope.ServiceProvider.GetRequiredService<NowFound.Application.Users.IAccountStatusCache>().Invalidate(user.Id);
    }

    private async Task<string> UserIdAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static string PathOf(HttpResponseMessage response) =>
        response.Headers.Location!.IsAbsoluteUri ? response.Headers.Location.PathAndQuery : response.Headers.Location.OriginalString;

    // ---- パスワードの有効期間 ----

    [Fact]
    public async Task ExpiredPassword_IsSentToTheChangePage_AtLogin_AndUntilChanged()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail);
        await SetPolicyAsync(admin, maxAgeDays: 30, historyCount: 0);
        await SetPasswordAgeAsync(UserEmail, ageDays: 40);
        var user = _factory.CreateBrowserLikeClient();

        var login = await user.PostFormAsync("/Account/Login", "/Account/Login", [new("Email", UserEmail), new("Password", UserPassword)]);
        var other = await user.GetAsync("/", TestContext.Current.CancellationToken);
        var changePage = await user.GetStringAsync("/Account/ChangePassword?expired=true", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("/Account/ChangePassword?expired=true", PathOf(login));
        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
        Assert.Contains("/Account/ChangePassword?expired=true", PathOf(other));
        Assert.Contains("有効期間が過ぎています", changePage);

        var change = await user.PostFormAsync("/Account/ChangePassword", "/Account/ChangePassword",
            [new("CurrentPassword", UserPassword), new("NewPassword", "Passw0rd!New"), new("ConfirmPassword", "Passw0rd!New")]);

        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task ExpiredPassword_LogoutIsStillAvailable()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail);
        await SetPolicyAsync(admin, maxAgeDays: 30, historyCount: 0);
        await SetPasswordAgeAsync(UserEmail, ageDays: 40);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync(UserEmail, UserPassword);

        var logout = await user.PostFormAsync("/Account/Logout", "/Account/ChangePassword", []);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Contains("/Account/Login", PathOf(logout));
    }

    [Fact]
    public async Task PasswordExpiring_DuringASession_IsEnforcedOnTheNextRequest()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync(UserEmail, UserPassword);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);

        await SetPolicyAsync(admin, maxAgeDays: 30, historyCount: 0);
        await SetPasswordAgeAsync(UserEmail, ageDays: 31);
        var after = await user.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/Account/ChangePassword?expired=true", PathOf(after));
    }

    [Fact]
    public async Task OldPassword_IsFine_WhenTheLifetimeIsUnlimited()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail);
        await SetPasswordAgeAsync(UserEmail, ageDays: 5000);
        var user = _factory.CreateBrowserLikeClient();

        await user.LoginAsync(UserEmail, UserPassword);

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task PasswordWithinTheLifetime_IsNotRedirected()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail);
        await SetPolicyAsync(admin, maxAgeDays: 30, historyCount: 0);
        await SetPasswordAgeAsync(UserEmail, ageDays: 10);
        var user = _factory.CreateBrowserLikeClient();

        await user.LoginAsync(UserEmail, UserPassword);

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }

    // ---- 再利用の禁止 ----

    [Fact]
    public async Task ChangePassword_ToARecentPassword_IsRejected()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail);
        await SetPolicyAsync(admin, maxAgeDays: 0, historyCount: 3);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync(UserEmail, UserPassword);

        var same = await user.PostFormAsync("/Account/ChangePassword", "/Account/Preferences",
            [new("CurrentPassword", UserPassword), new("NewPassword", UserPassword), new("ConfirmPassword", UserPassword)]);
        var fresh = await user.PostFormAsync("/Account/ChangePassword", "/Account/Preferences",
            [new("CurrentPassword", UserPassword), new("NewPassword", "Passw0rd!New"), new("ConfirmPassword", "Passw0rd!New")]);
        var back = await user.PostFormAsync("/Account/ChangePassword", "/Account/Preferences",
            [new("CurrentPassword", "Passw0rd!New"), new("NewPassword", UserPassword), new("ConfirmPassword", UserPassword)]);

        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Contains("再利用できません", await BodyAsync(same));
        Assert.Equal(HttpStatusCode.Redirect, fresh.StatusCode);
        Assert.Contains("再利用できません", await BodyAsync(back));
    }

    [Fact]
    public async Task PolicyPage_SavesThePasswordLifetimeAndHistory()
    {
        var admin = await AdminAsync();

        var save = await SetPolicyAsync(admin, maxAgeDays: 90, historyCount: 5);
        var page = await admin.GetStringAsync("/Settings/AccountPolicy", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Contains("value=\"90\"", page);
        Assert.Contains("value=\"5\"", page);
    }

    [Theory]
    [InlineData(1000, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 25)]
    public async Task PolicyPage_RejectsOutOfRangeLifetimeAndHistory(int maxAgeDays, int historyCount)
    {
        var admin = await AdminAsync();

        var response = await SetPolicyAsync(admin, maxAgeDays, historyCount);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("指定してください", await BodyAsync(response));
    }

    // ---- 削除 ----

    [Fact]
    public async Task DeletingAUser_RemovesThem_AndEndsTheirSession()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync(UserEmail, UserPassword);
        var id = await UserIdAsync(UserEmail);

        var delete = await admin.PostFormAsync("/Users/Delete", $"/Users/Edit?id={id}", [new("id", id)]);
        var list = await admin.GetStringAsync("/Users", TestContext.Current.CancellationToken);
        var after = await user.GetAsync("/", TestContext.Current.CancellationToken);
        var relogin = await BodyAsync(await user.PostFormAsync("/Account/Login", "/Account/Login", [new("Email", UserEmail), new("Password", UserPassword)]));

        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        Assert.DoesNotContain(UserEmail + "</td>", list);
        Assert.Contains("を削除しました", list);
        Assert.Contains("/Account/Login?blocked=true", PathOf(after));
        Assert.Contains("正しくありません", relogin);
    }

    [Fact]
    public async Task DeleteButton_IsShown_ForOtherUsers_ButNotForYourself()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail);
        var userId = await UserIdAsync(UserEmail);
        var adminId = await UserIdAsync("admin@nowfound.invalid");

        var other = await admin.GetStringAsync($"/Users/Edit?id={userId}", TestContext.Current.CancellationToken);
        var self = await admin.GetStringAsync($"/Users/Edit?id={adminId}", TestContext.Current.CancellationToken);

        Assert.Contains("このユーザーを削除", other);
        Assert.DoesNotContain("このユーザーを削除", self);
        Assert.Contains("自分自身は削除できません", self);
    }

    [Fact]
    public async Task TheOnlyAdmin_CannotBeDeleted()
    {
        var admin = await AdminAsync();
        var adminId = await UserIdAsync("admin@nowfound.invalid");

        var delete = await admin.PostFormAsync("/Users/Delete", $"/Users/Edit?id={adminId}", [new("id", adminId)]);
        var edit = await admin.GetStringAsync(PathOf(delete), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        Assert.Contains("/Users/Edit", PathOf(delete));
        Assert.Contains("削除できません", edit);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task AnAdmin_CanBeDeleted_WhenAnotherActiveAdminRemains()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, "admin2@example.com", isAdmin: true);
        var secondId = await UserIdAsync("admin2@example.com");

        var delete = await admin.PostFormAsync("/Users/Delete", $"/Users/Edit?id={secondId}", [new("id", secondId)]);
        var list = await admin.GetStringAsync("/Users", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        Assert.Contains("/Users", PathOf(delete));
        Assert.DoesNotContain("admin2@example.com</td>", list);
    }

    [Fact]
    public async Task DeletingAnUnknownUser_IsNotFound()
    {
        var admin = await AdminAsync();

        var response = await admin.PostFormAsync("/Users/Delete", "/Users", [new("id", "nope")]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NormalUsers_CannotDelete()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail);
        await CreateUserAsync(admin, "other@example.com");
        var otherId = await UserIdAsync("other@example.com");
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync(UserEmail, UserPassword);

        var response = await user.PostFormAsync("/Users/Delete", "/Account/Preferences", [new("id", otherId)]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", PathOf(response));
        Assert.Contains("other@example.com", await admin.GetStringAsync("/Users", TestContext.Current.CancellationToken));
    }
}
