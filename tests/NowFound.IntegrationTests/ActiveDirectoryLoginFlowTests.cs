using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NowFound.Application.Users;
using NowFound.Infrastructure.Identity;

namespace NowFound.IntegrationTests;

/// <summary>ActiveDirectory 認証（ログイン画面のタブ、AD ユーザーの登録とログイン）。LDAP の代わりに偽の認証器を使う。</summary>
public sealed partial class ActiveDirectoryLoginFlowTests : IDisposable
{
    private const string UserName = "kishi";
    private const string UserPassword = "Ad-Passw0rd!";

    private readonly WebAppFactory _baseFactory = new();
    private readonly FakeDirectoryAuthenticator _directory = new();
    private readonly HttpClient _admin;

    public ActiveDirectoryLoginFlowTests()
    {
        _directory.Accounts[UserName] = UserPassword;
        Factory = _baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDirectoryAuthenticator>();
            services.AddSingleton<IDirectoryAuthenticator>(_directory);
        }));
        _admin = NewClient();
    }

    private WebApplicationFactory<Program> Factory { get; }

    private HttpClient NewClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    public void Dispose()
    {
        Factory.Dispose();
        _baseFactory.Dispose();
    }

    private async Task AdminLoginAsync() => await _admin.LoginAsAdminAsync();

    private Task<HttpResponseMessage> SaveSettingsAsync(
        bool enabled, string server = "dc01.corp.test", string port = "389", string security = "None", string domain = "corp.test") =>
        _admin.PostFormAsync("/Settings/ActiveDirectory", "/Settings/ActiveDirectory",
        [
            new("Enabled", enabled ? "true" : "false"),
            new("Server", server),
            new("Port", port),
            new("Security", security),
            new("Domain", domain),
        ]);

    private Task<HttpResponseMessage> CreateDirectoryUserAsync(string name, bool enabled = true) =>
        _admin.PostFormAsync("/Users/Create", "/Users/Create",
        [
            new("AuthSource", "ActiveDirectory"),
            new("Email", name),
            new("Password", string.Empty),
            new("IsAdmin", "false"),
            new("RequirePasswordChange", "false"),
            new("IsEnabled", enabled ? "true" : "false"),
            new("ExpiresOn", string.Empty),
        ]);

    /// <summary>管理者が AD 認証を有効にし、AD ユーザーを 1 人登録した状態にする。</summary>
    private async Task SetUpAsync(bool enabled = true)
    {
        await AdminLoginAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SaveSettingsAsync(enabled)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await CreateDirectoryUserAsync(UserName)).StatusCode);
    }

    private static Task<HttpResponseMessage> DirectoryLoginAsync(HttpClient client, string userName, string password) =>
        client.PostFormAsync("/Account/Login", "/Account/Login",
            [new("AuthSource", "ActiveDirectory"), new("UserName", userName), new("Password", password)]);

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task LoginPage_WithoutActiveDirectory_HasNoTabs()
    {
        var html = await _admin.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("data-auth-tab", html);
        Assert.Contains("name=\"Email\"", html);
        Assert.DoesNotContain("name=\"UserName\"", html);
    }

    [Fact]
    public async Task LoginPage_WithActiveDirectory_ShowsStandardAndDirectoryTabs()
    {
        await SetUpAsync();

        var html = await NewClient().GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        Assert.Contains("data-auth-tab=\"Local\"", html);
        Assert.Contains("data-auth-tab=\"ActiveDirectory\"", html);
        Assert.Contains("標準", html);
        Assert.Contains("name=\"UserName\"", html);
        // 既定は標準タブ。
        Assert.Contains("id=\"tab-local\"", html);
        Assert.Matches("id=\"tab-local\"[^>]*aria-selected=\"true\"", html);
    }

    [Fact]
    public async Task LoginPage_WhenEnabledButIncomplete_StaysStandardOnly()
    {
        await AdminLoginAsync();
        // 無効のまま保存（サーバー・ドメイン未入力でも可）。
        Assert.Equal(HttpStatusCode.Redirect, (await SaveSettingsAsync(false, server: "", domain: "")).StatusCode);

        var html = await NewClient().GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("data-auth-tab", html);
    }

    [Fact]
    public async Task DirectoryLogin_WithRightPassword_SignsInAndHidesPasswordChange()
    {
        await SetUpAsync();
        var client = NewClient();

        var response = await DirectoryLoginAsync(client, UserName, UserPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.Equal(("dc01.corp.test", UserName), (_directory.LastServer, _directory.LastUserName));
        Assert.Equal("corp.test", _directory.LastDomain);

        var preferences = await client.GetAsync("/Account/Preferences", TestContext.Current.CancellationToken);
        var html = await BodyAsync(preferences);
        Assert.Equal(HttpStatusCode.OK, preferences.StatusCode);
        Assert.DoesNotContain("id=\"CurrentPassword\"", html);
        Assert.Contains("ActiveDirectory 側で管理", html);
    }

    [Fact]
    public async Task DirectoryLogin_AcceptsDomainPrefix()
    {
        await SetUpAsync();

        var response = await DirectoryLoginAsync(NewClient(), @"CORP\" + UserName, UserPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(UserName, _directory.LastUserName);
    }

    [Fact]
    public async Task DirectoryLogin_WithWrongPassword_IsRejected()
    {
        await SetUpAsync();

        var response = await DirectoryLoginAsync(NewClient(), UserName, "wrong");
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ユーザー名またはパスワードが正しくありません", html);
        // 失敗したときも ActiveDirectory のタブのまま再表示する。
        Assert.Matches("id=\"tab-directory\"[^>]*aria-selected=\"true\"", html);
    }

    [Fact]
    public async Task DirectoryLogin_RepeatedFailures_LockTheAccount_EvenForTheRightPassword()
    {
        await SetUpAsync();
        var client = NewClient();

        for (var i = 0; i < 5; i++)
        {
            await DirectoryLoginAsync(client, UserName, "wrong");
        }

        _directory.Calls = 0;
        var locked = await DirectoryLoginAsync(client, UserName, UserPassword);
        var html = await BodyAsync(locked);

        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Contains("ロックされています", html);
        // ロック中は ActiveDirectory に問い合わせない（ロックアウトの肩代わりで AD 側のアカウントを巻き込まない）。
        Assert.Equal(0, _directory.Calls);
    }

    [Fact]
    public async Task DirectoryLogin_SuccessResetsTheFailureCount()
    {
        await SetUpAsync();
        var client = NewClient();

        for (var i = 0; i < 4; i++)
        {
            await DirectoryLoginAsync(client, UserName, "wrong");
        }

        Assert.Equal(HttpStatusCode.Redirect, (await DirectoryLoginAsync(client, UserName, UserPassword)).StatusCode);
        for (var i = 0; i < 4; i++)
        {
            await DirectoryLoginAsync(client, UserName, "wrong");
        }

        Assert.Equal(HttpStatusCode.Redirect, (await DirectoryLoginAsync(NewClient(), UserName, UserPassword)).StatusCode);
    }

    [Fact]
    public async Task DirectoryLogin_UnregisteredUser_IsRejectedWithoutAskingTheDirectory()
    {
        await SetUpAsync();
        _directory.Accounts["stranger"] = UserPassword;
        _directory.Calls = 0;

        var response = await DirectoryLoginAsync(NewClient(), "stranger", UserPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ユーザー名またはパスワードが正しくありません", await BodyAsync(response));
        Assert.Equal(0, _directory.Calls);
    }

    [Fact]
    public async Task DirectoryLogin_WhenServerIsUnreachable_ShowsAnErrorAndDoesNotCountAsAFailure()
    {
        await SetUpAsync();
        var client = NewClient();
        _directory.Unavailable = true;

        for (var i = 0; i < 6; i++)
        {
            var down = await DirectoryLoginAsync(client, UserName, UserPassword);
            Assert.Contains("接続できません", await BodyAsync(down));
        }

        _directory.Unavailable = false;
        Assert.Equal(HttpStatusCode.Redirect, (await DirectoryLoginAsync(client, UserName, UserPassword)).StatusCode);
    }

    [Fact]
    public async Task DirectoryLogin_DisabledUser_GetsTheReasonOnlyWithTheRightPassword()
    {
        await AdminLoginAsync();
        await SaveSettingsAsync(true);
        await CreateDirectoryUserAsync(UserName, enabled: false);

        var right = await BodyAsync(await DirectoryLoginAsync(NewClient(), UserName, UserPassword));
        var wrong = await BodyAsync(await DirectoryLoginAsync(NewClient(), UserName, "wrong"));

        Assert.Contains("無効です", right);
        Assert.DoesNotContain("無効です", wrong);
    }

    [Fact]
    public async Task DirectoryLogin_WhenActiveDirectoryIsDisabled_IsRefused()
    {
        await SetUpAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SaveSettingsAsync(false)).StatusCode);
        _directory.Calls = 0;

        var response = await DirectoryLoginAsync(NewClient(), UserName, UserPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _directory.Calls);
    }

    [Fact]
    public async Task DirectoryUser_CannotSignInThroughTheStandardTab()
    {
        await SetUpAsync();

        var response = await NewClient().PostFormAsync("/Account/Login", "/Account/Login",
            [new("AuthSource", "Local"), new("Email", UserName), new("Password", UserPassword)]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _directory.Calls);
    }

    [Fact]
    public async Task StandardLogin_StillWorks_WhenActiveDirectoryIsEnabled()
    {
        await SetUpAsync();

        var response = await NewClient().PostFormAsync("/Account/Login", "/Account/Login",
            [new("AuthSource", "Local"), new("Email", "admin@nowfound.invalid"), new("Password", "NewPassw0rd!")]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task DirectoryUser_SessionEndsWhenTheAdministratorDisablesThem()
    {
        await SetUpAsync();
        var client = NewClient();
        await DirectoryLoginAsync(client, UserName, UserPassword);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/", TestContext.Current.CancellationToken)).StatusCode);

        string id;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            id = (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync("ad:" + UserName))!.Id;
        }

        await _admin.PostFormAsync("/Users/Edit", $"/Users/Edit?id={id}",
            [new("Id", id), new("IsEnabled", "false"), new("IsAdmin", "false"), new("ExpiresOn", "")]);
        var after = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/Account/Login?blocked=true", after.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task DirectoryUser_PasswordChangeEndpoint_IsRefused()
    {
        await SetUpAsync();
        var client = NewClient();
        await DirectoryLoginAsync(client, UserName, UserPassword);

        var response = await client.PostFormAsync("/Account/ChangePassword", "/Account/Preferences",
        [
            new("CurrentPassword", ""),
            new("NewPassword", "NewPassw0rd!"),
            new("ConfirmPassword", "NewPassw0rd!"),
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ActiveDirectory 側で変更", await BodyAsync(response));
    }

    [Fact]
    public async Task UsersList_ShowsTheAuthenticationMethod()
    {
        await SetUpAsync();

        var html = await _admin.GetStringAsync("/Users", TestContext.Current.CancellationToken);

        Assert.Contains(">kishi<", html);
        Assert.Contains("data-col=\"auth\"", html);
    }

    [Fact]
    public async Task CreateDirectoryUser_WithInvalidName_ShowsAnError()
    {
        await AdminLoginAsync();

        var response = await CreateDirectoryUserAsync("kishi@corp.test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ユーザー名（sAMAccountName）が正しくありません", await BodyAsync(response));
    }

    [Fact]
    public async Task Settings_EnabledWithoutServer_IsRejected()
    {
        await AdminLoginAsync();

        var response = await SaveSettingsAsync(true, server: "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("サーバーとドメインを入力してください", await BodyAsync(response));
    }

    [Fact]
    public async Task Settings_Page_IsAdminOnly()
    {
        var anonymous = await NewClient().GetAsync("/Settings/ActiveDirectory", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains("/Account/Login", anonymous.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Settings_SavedValues_AreShownAgain()
    {
        await AdminLoginAsync();
        await SaveSettingsAsync(true, server: "dc02.corp.test", port: "636", security: "Ssl", domain: "corp.test");

        var html = await _admin.GetStringAsync("/Settings/ActiveDirectory", TestContext.Current.CancellationToken);

        Assert.Contains("value=\"dc02.corp.test\"", html);
        Assert.Contains("value=\"636\"", html);
        Assert.Matches("<option value=\"Ssl\"[^>]*selected", html);
    }

    [Fact]
    public async Task LoginPage_WhenAuthCookieIsActiveDirectory_RendersDirectoryTabAsActive()
    {
        await SetUpAsync();
        var client = NewClient();
        client.DefaultRequestHeaders.Add("Cookie", "nowfound-login-auth=ActiveDirectory");

        var html = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        Assert.Matches(@"id=""tab-directory""[^>]*aria-selected=""true""", html);
        Assert.Matches(@"id=""tab-local""[^>]*aria-selected=""false""", html);
        // panel-directory は表示（hiddenなし）、panel-local は非表示（hidden属性あり）
        Assert.Matches(@"id=""panel-directory""(?![^>]*hidden)", html);
        Assert.Matches(@"id=""panel-local""[^>]*hidden", html);
    }

    [Fact]
    public async Task LoginPage_WhenDirectoryDisabled_CookieIsIgnoredAndRendersLocalOnly()
    {
        // AD 未設定の初期状態
        var client = NewClient();
        client.DefaultRequestHeaders.Add("Cookie", "nowfound-login-auth=ActiveDirectory");

        var html = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("data-auth-tab", html);
        Assert.Contains("name=\"Email\"", html);
        Assert.DoesNotContain("name=\"UserName\"", html);
    }

    [Fact]
    public async Task DirectoryLogin_SetsAuthSourceCookieToActiveDirectory()
    {
        await SetUpAsync();
        var client = NewClient();

        var response = await DirectoryLoginAsync(client, UserName, UserPassword);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // レスポンスの Set-Cookie に nowfound-login-auth=ActiveDirectory が含まれること
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join(";", values) : string.Empty;
        Assert.Contains("nowfound-login-auth=ActiveDirectory", setCookies);

        // 同じクライアントでログアウト後に再度ログイン画面へアクセスした際、ActiveDirectory タブが選択状態であること
        await client.PostFormAsync("/Account/Logout", "/", []);
        var html = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);
        Assert.Matches(@"id=""tab-directory""[^>]*aria-selected=""true""", html);
    }

    [Fact]
    public async Task LocalLogin_SetsAuthSourceCookieToLocal()
    {
        await SetUpAsync();
        var client = NewClient();

        // 以前 AD を選んでいた Cookie をセット
        client.DefaultRequestHeaders.Add("Cookie", "nowfound-login-auth=ActiveDirectory");

        // 標準ログインを実行（SetUpAsync で管理者のパスワードは NewPassw0rd! に更新されている）
        var response = await client.PostFormAsync("/Account/Login", "/Account/Login",
        [
            new("Email", "admin@nowfound.invalid"),
            new("Password", "NewPassw0rd!"),
            new("AuthSource", "Local"),
        ]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // レスポンスの Set-Cookie に nowfound-login-auth=Local が含まれること
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join(";", values) : string.Empty;
        Assert.Contains("nowfound-login-auth=Local", setCookies);
    }

    /// <summary>LDAP に接続せず、登録済みアカウントの表で判定する認証器。</summary>
    private sealed partial class FakeDirectoryAuthenticator : IDirectoryAuthenticator
    {
        public Dictionary<string, string> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Unavailable { get; set; }

        public int Calls { get; set; }

        public string? LastServer { get; private set; }

        public string? LastDomain { get; private set; }

        public string? LastUserName { get; private set; }

        public Task<DirectoryAuthStatus> AuthenticateAsync(
            ActiveDirectorySettings settings, string userName, string password, CancellationToken ct = default)
        {
            Calls++;
            LastServer = settings.Server;
            LastDomain = settings.Domain;
            LastUserName = userName;
            if (Unavailable)
            {
                return Task.FromResult(DirectoryAuthStatus.Unavailable);
            }

            return Task.FromResult(
                Accounts.TryGetValue(userName, out var expected) && expected == password
                    ? DirectoryAuthStatus.Success
                    : DirectoryAuthStatus.InvalidCredentials);
        }
    }
}
