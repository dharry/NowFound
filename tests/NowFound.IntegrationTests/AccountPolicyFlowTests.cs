using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Infrastructure.Identity;

namespace NowFound.IntegrationTests;

/// <summary>アカウントポリシー（パスワード規則・ロック回数）と、ユーザーの有効/無効・有効期限。</summary>
public sealed class AccountPolicyFlowTests : IDisposable
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

    private static async Task<HttpResponseMessage> CreateUserAsync(
        HttpClient admin, string email, string password, string? expiresOn = null, bool enabled = true) =>
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
        [
            new("Email", email),
            new("Password", password),
            new("IsAdmin", "false"),
            new("RequirePasswordChange", "false"),
            new("IsEnabled", enabled ? "true" : "false"),
            new("ExpiresOn", expiresOn ?? string.Empty),
        ]);

    private static Task<HttpResponseMessage> SetPolicyAsync(
        HttpClient admin, int minLength, bool complexity, int maxFailed, int maxAgeDays = 0, int historyCount = 0) =>
        admin.PostFormAsync("/Settings/AccountPolicy", "/Settings/AccountPolicy",
        [
            new("PasswordMinLength", minLength.ToString()),
            new("RequireComplexity", complexity ? "true" : "false"),
            new("MaxFailedAttempts", maxFailed.ToString()),
            new("PasswordMaxAgeDays", maxAgeDays.ToString()),
            new("PasswordHistoryCount", historyCount.ToString()),
        ]);

    private static Task<HttpResponseMessage> TryLoginAsync(HttpClient client, string email, string password) =>
        client.PostFormAsync("/Account/Login", "/Account/Login", [new("Email", email), new("Password", password)]);

    private async Task<string> UserIdAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email))!.Id;
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static string DateString(int offsetDays) => DateOnly.FromDateTime(DateTime.Now).AddDays(offsetDays).ToString("yyyy-MM-dd");

    [Fact]
    public async Task DisabledUser_CannotLogIn_AndGetsTheReasonOnlyWithTheRightPassword()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail, UserPassword, enabled: false);

        var right = await BodyAsync(await TryLoginAsync(_factory.CreateBrowserLikeClient(), UserEmail, UserPassword));
        var wrong = await BodyAsync(await TryLoginAsync(_factory.CreateBrowserLikeClient(), UserEmail, "Wr0ng-password"));

        Assert.Contains("無効です", right);
        Assert.DoesNotContain("無効です", wrong);
        Assert.Contains("正しくありません", wrong);
    }

    [Fact]
    public async Task ExpiredUser_CannotLogIn_ButTodayIsStillValid()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail, UserPassword, expiresOn: DateString(-1));
        await CreateUserAsync(admin, "today@example.com", UserPassword, expiresOn: DateString(0));

        var expired = await BodyAsync(await TryLoginAsync(_factory.CreateBrowserLikeClient(), UserEmail, UserPassword));
        var today = await TryLoginAsync(_factory.CreateBrowserLikeClient(), "today@example.com", UserPassword);

        Assert.Contains("有効期限が切れています", expired);
        Assert.Equal(HttpStatusCode.Redirect, today.StatusCode);
    }

    [Fact]
    public async Task DisablingAUser_SignsOutTheirExistingSession_OnTheNextRequest()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail, UserPassword);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync(UserEmail, UserPassword);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
        var id = await UserIdAsync(UserEmail);

        var update = await admin.PostFormAsync("/Users/Edit", $"/Users/Edit?id={id}",
            [new("Id", id), new("IsEnabled", "false"), new("IsAdmin", "false"), new("ExpiresOn", "")]);
        var after = await user.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, update.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/Account/Login?blocked=true", after.Headers.Location!.OriginalString);
        var loginResponse = await user.GetAsync(after.Headers.Location.OriginalString, TestContext.Current.CancellationToken);
        var login = await BodyAsync(loginResponse);
        Assert.True(loginResponse.StatusCode == HttpStatusCode.OK, $"status={loginResponse.StatusCode} location={loginResponse.Headers.Location}");
        Assert.Contains("ログアウトしました", login);
        var anonymous = await user.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Equal("/Account/Login", anonymous.Headers.Location!.IsAbsoluteUri ? anonymous.Headers.Location.AbsolutePath : anonymous.Headers.Location.OriginalString.Split('?')[0]);
    }

    [Fact]
    public async Task RevokingAdminRole_RejectsTheExistingSession_OnTheNextRequest()
    {
        var admin = await AdminAsync();
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
        [
            new("Email", UserEmail), new("Password", UserPassword), new("IsAdmin", "true"),
            new("RequirePasswordChange", "false"), new("IsEnabled", "true"), new("ExpiresOn", string.Empty),
        ]);
        var other = _factory.CreateBrowserLikeClient();
        await other.LoginAsync(UserEmail, UserPassword);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/Users", TestContext.Current.CancellationToken)).StatusCode);
        var id = await UserIdAsync(UserEmail);

        var update = await admin.PostFormAsync("/Users/Edit", $"/Users/Edit?id={id}",
            [new("Id", id), new("IsEnabled", "true"), new("IsAdmin", "false"), new("ExpiresOn", "")]);
        var after = await other.GetAsync("/Users", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, update.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/Account/Login", after.Headers.Location!.OriginalString);
        var anonymous = await other.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
    }

    [Fact]
    public async Task ReEnablingAUser_AllowsLoginAgain()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail, UserPassword, enabled: false);
        var id = await UserIdAsync(UserEmail);

        await admin.PostFormAsync("/Users/Edit", $"/Users/Edit?id={id}",
            [new("Id", id), new("IsEnabled", "true"), new("IsAdmin", "false"), new("ExpiresOn", "")]);

        var login = await TryLoginAsync(_factory.CreateBrowserLikeClient(), UserEmail, UserPassword);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    [Fact]
    public async Task Admin_CannotDisableTheirOwnAccount()
    {
        var admin = await AdminAsync();
        var id = await UserIdAsync("admin@nowfound.invalid");

        var response = await admin.PostFormAsync("/Users/Edit", $"/Users/Edit?id={id}",
            [new("Id", id), new("IsEnabled", "false"), new("IsAdmin", "true"), new("ExpiresOn", "")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("自分自身", await BodyAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task UsersList_ShowsStatusAndExpiry_AndEditPageOpens()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, "off@example.com", UserPassword, enabled: false);
        await CreateUserAsync(admin, "old@example.com", UserPassword, expiresOn: DateString(-3));
        var id = await UserIdAsync("off@example.com");

        var list = await admin.GetStringAsync("/Users", TestContext.Current.CancellationToken);
        var edit = await admin.GetStringAsync($"/Users/Edit?id={id}", TestContext.Current.CancellationToken);

        Assert.Contains("無効", list);
        Assert.Contains("期限切れ", list);
        Assert.Contains(DateString(-3), list);
        Assert.Contains("off@example.com", edit);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/Users/Edit?id=nope", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Lockout_HappensAfterTheConfiguredFailures_AndAdminCanUnlock()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail, UserPassword);
        await SetPolicyAsync(admin, 8, true, 3);
        var user = _factory.CreateBrowserLikeClient();

        for (var i = 0; i < 3; i++)
        {
            await TryLoginAsync(user, UserEmail, "Wr0ng-password");
        }

        var locked = await BodyAsync(await TryLoginAsync(user, UserEmail, UserPassword));
        var id = await UserIdAsync(UserEmail);
        var editPage = await admin.GetStringAsync($"/Users/Edit?id={id}", TestContext.Current.CancellationToken);
        await admin.PostFormAsync("/Users/Unlock", $"/Users/Edit?id={id}", [new("id", id), new("email", UserEmail)]);
        var afterUnlock = await TryLoginAsync(user, UserEmail, UserPassword);

        Assert.Contains("ロックされています", locked);
        Assert.Contains("ロックを解除", editPage);
        Assert.Equal(HttpStatusCode.Redirect, afterUnlock.StatusCode);
    }

    [Fact]
    public async Task Lockout_IsNotTriggeredEarly()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail, UserPassword);
        await SetPolicyAsync(admin, 8, true, 3);
        var user = _factory.CreateBrowserLikeClient();

        await TryLoginAsync(user, UserEmail, "Wr0ng-password");
        await TryLoginAsync(user, UserEmail, "Wr0ng-password");

        Assert.Equal(HttpStatusCode.Redirect, (await TryLoginAsync(user, UserEmail, UserPassword)).StatusCode);
    }

    [Fact]
    public async Task Lockout_Zero_NeverLocksTheAccount()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail, UserPassword);
        await SetPolicyAsync(admin, 8, true, 0);
        var user = _factory.CreateBrowserLikeClient();

        for (var i = 0; i < 15; i++)
        {
            await TryLoginAsync(user, UserEmail, "Wr0ng-password");
        }

        Assert.Equal(HttpStatusCode.Redirect, (await TryLoginAsync(user, UserEmail, UserPassword)).StatusCode);
    }

    [Fact]
    public async Task PolicyPage_SavesTheValues_AndShowsThemAgain()
    {
        var admin = await AdminAsync();

        var save = await SetPolicyAsync(admin, 12, false, 7);
        var page = await admin.GetStringAsync("/Settings/AccountPolicy", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Contains("value=\"12\"", page);
        Assert.Contains("value=\"7\"", page);
        Assert.DoesNotContain("checked", page.Split("RequireComplexity")[1].Split('>')[0]);
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(128, 5)]
    [InlineData(8, 11)]
    [InlineData(8, -1)]
    public async Task PolicyPage_RejectsOutOfRangeValues(int minLength, int maxFailed)
    {
        var admin = await AdminAsync();

        var response = await SetPolicyAsync(admin, minLength, true, maxFailed);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("指定してください", await BodyAsync(response));
    }

    [Fact]
    public async Task PasswordRules_FollowThePolicy_WhenCreatingUsers()
    {
        var admin = await AdminAsync();
        await SetPolicyAsync(admin, 12, true, 5);

        var tooShort = await BodyAsync(await CreateUserAsync(admin, "a@example.com", "Abcdef1!"));
        var noComplexity = await BodyAsync(await CreateUserAsync(admin, "b@example.com", "abcdefghijklm"));
        var ok = await CreateUserAsync(admin, "c@example.com", "Abcdefghij12!");

        Assert.Contains("12 文字以上", tooShort);
        Assert.Contains("3 種類以上", noComplexity);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
    }

    [Fact]
    public async Task PasswordHint_IsShownOnTheCreateUserAndUserSettingsPages()
    {
        var admin = await AdminAsync();
        await SetPolicyAsync(admin, 10, true, 5);

        var create = await admin.GetStringAsync("/Users/Create", TestContext.Current.CancellationToken);
        var change = await admin.GetStringAsync("/Account/Preferences", TestContext.Current.CancellationToken);

        Assert.Contains("10 文字以上", create);
        Assert.Contains("10 文字以上", change);
        Assert.Contains("3 種類以上", create);
    }

    [Fact]
    public async Task PolicyPage_IsForbiddenForNormalUsers()
    {
        var admin = await AdminAsync();
        await CreateUserAsync(admin, UserEmail, UserPassword);
        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync(UserEmail, UserPassword);

        var response = await user.GetAsync("/Settings/AccountPolicy", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location!.OriginalString);
    }
}
