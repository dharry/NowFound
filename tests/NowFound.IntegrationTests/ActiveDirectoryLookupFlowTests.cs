using System.Net;
using NowFound.Application.Users;

namespace NowFound.IntegrationTests;

/// <summary>ActiveDirectory 設定の「接続確認」と、ユーザー追加の「ユーザーの存在確認」。</summary>
public sealed partial class ActiveDirectoryLoginFlowTests
{
    private Task<HttpResponseMessage> TestConnectionAsync(string bindPassword = "", string bindUser = "svc-lookup") =>
        _admin.PostFormAsync("/Settings/ActiveDirectoryTest", "/Settings/ActiveDirectory",
        [
            new("Server", "dc-typed.corp.test"),
            new("Port", "636"),
            new("Security", "Ssl"),
            new("Domain", "corp.test"),
            new("BindUser", bindUser),
            new("BindPassword", bindPassword),
            new("SearchBase", ""),
        ]);

    private Task<HttpResponseMessage> SaveLookupSettingsAsync(string bindUser, string bindPassword) =>
        _admin.PostFormAsync("/Settings/ActiveDirectory", "/Settings/ActiveDirectory",
        [
            new("Enabled", "true"),
            new("Server", "dc01.corp.test"),
            new("Port", "389"),
            new("Security", "None"),
            new("Domain", "corp.test"),
            new("BindUser", bindUser),
            new("BindPassword", bindPassword),
            new("SearchBase", ""),
        ]);

    private Task<HttpResponseMessage> CheckUserAsync(string name) =>
        _admin.PostFormAsync("/Users/CheckDirectoryUser", "/Users/Create", [new("email", name)]);

    private static async Task<(bool Success, string Message)> JsonResultAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await BodyAsync(response));
        return (json.RootElement.GetProperty("success").GetBoolean(), json.RootElement.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task ConnectionCheck_UsesTheTypedValues_WithoutSavingThem()
    {
        await AdminLoginAsync();
        _directory.CheckResult = new DirectoryCheckResult(DirectoryCheckStatus.Success, BindTested: true);

        var (success, message) = await JsonResultAsync(await TestConnectionAsync("typed-pw"));

        Assert.True(success);
        Assert.Contains("接続できました", message);
        Assert.Equal("dc-typed.corp.test", _directory.LastCheckedSettings!.Server);
        Assert.Equal(636, _directory.LastCheckedSettings.Port);
        Assert.Equal(DirectorySecurity.Ssl, _directory.LastCheckedSettings.Security);
        Assert.Equal("typed-pw", _directory.LastBindPassword);
        var page = await _admin.GetStringAsync("/Settings/ActiveDirectory", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("dc-typed.corp.test", page);
    }

    [Fact]
    public async Task ConnectionCheck_Failure_ShowsTheReason()
    {
        await AdminLoginAsync();
        _directory.CheckResult = new DirectoryCheckResult(DirectoryCheckStatus.ConnectFailed, Detail: "boom: handshake reset");

        var (success, message) = await JsonResultAsync(await TestConnectionAsync());

        Assert.False(success);
        Assert.Contains("接続できませんでした", message);
        Assert.Contains("boom: handshake reset", message);
    }

    [Fact]
    public async Task ConnectionCheck_RejectedLookupAccount_IsReportedSeparately()
    {
        await AdminLoginAsync();
        _directory.CheckResult = new DirectoryCheckResult(DirectoryCheckStatus.BindRejected, true, "49");

        var (success, message) = await JsonResultAsync(await TestConnectionAsync("pw"));

        Assert.False(success);
        Assert.Contains("資格情報が拒否されました", message);
    }

    [Fact]
    public async Task ConnectionCheck_WithAnInvalidValue_ReportsAValidationError()
    {
        await AdminLoginAsync();

        var (success, message) = await JsonResultAsync(await _admin.PostFormAsync(
            "/Settings/ActiveDirectoryTest", "/Settings/ActiveDirectory",
            [new("Server", "bad host!"), new("Port", "389"), new("Security", "None"), new("Domain", "corp.test")]));

        Assert.False(success);
        Assert.Contains("ホスト名または IP アドレス", message);
        Assert.Equal(0, _directory.CheckCalls);
    }

    [Fact]
    public async Task ConnectionCheck_WithBlankPassword_UsesTheStoredOne()
    {
        await AdminLoginAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SaveLookupSettingsAsync("svc-lookup", "stored-pw")).StatusCode);

        await TestConnectionAsync(bindPassword: "");
        Assert.Equal("stored-pw", _directory.LastBindPassword);

        await TestConnectionAsync(bindPassword: "typed-pw");
        Assert.Equal("typed-pw", _directory.LastBindPassword);
    }

    [Fact]
    public async Task ConnectionCheck_RequiresAnAdministrator()
    {
        var response = await NewClient().PostAsync(
            "/Settings/ActiveDirectoryTest", new FormUrlEncodedContent([new("Server", "dc01")]), TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _directory.CheckCalls);
    }

    [Fact]
    public async Task Settings_NeverEchoTheLookupPassword_ButShowThatOneIsSet()
    {
        await AdminLoginAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SaveLookupSettingsAsync("svc-lookup", "Sup3r-Secret!")).StatusCode);

        var html = await _admin.GetStringAsync("/Settings/ActiveDirectory", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Sup3r-Secret!", html);
        Assert.Contains("value=\"svc-lookup\"", html);
        Assert.Contains("設定済み", html);
    }

    [Fact]
    public async Task Settings_LookupAccountWithoutAPassword_IsRejected()
    {
        await AdminLoginAsync();

        var response = await SaveLookupSettingsAsync("svc-lookup", "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("パスワードも入力してください", await BodyAsync(response));
    }

    [Fact]
    public async Task UserCheck_Found_ShowsTheDisplayName_AndUsesTheStoredLookupAccount()
    {
        await AdminLoginAsync();
        await SaveLookupSettingsAsync("svc-lookup", "stored-pw");
        _directory.LookupResult = new DirectoryLookupResult(DirectoryLookupStatus.Found, "岸 太郎");

        var (success, message) = await JsonResultAsync(await CheckUserAsync(@"CORP\kishi"));

        Assert.True(success);
        Assert.Contains("岸 太郎", message);
        Assert.Equal("kishi", _directory.LastLookupUser);
        Assert.Equal("stored-pw", _directory.LastBindPassword);
        Assert.Equal("svc-lookup", _directory.LastCheckedSettings!.BindUser);
    }

    [Fact]
    public async Task UserCheck_DisabledInTheDirectory_IsFlagged()
    {
        await AdminLoginAsync();
        _directory.LookupResult = new DirectoryLookupResult(DirectoryLookupStatus.Found, "岸 太郎", AccountDisabled: true);

        var (success, message) = await JsonResultAsync(await CheckUserAsync("kishi"));

        Assert.True(success);
        Assert.Contains("無効化されています", message);
    }

    [Theory]
    [InlineData(DirectoryLookupStatus.NotFound, "見つかりませんでした")]
    [InlineData(DirectoryLookupStatus.NotConfigured, "確認用アカウント")]
    [InlineData(DirectoryLookupStatus.BindRejected, "資格情報が拒否されました")]
    [InlineData(DirectoryLookupStatus.SearchFailed, "検索に失敗しました")]
    [InlineData(DirectoryLookupStatus.ConnectFailed, "接続できませんでした")]
    public async Task UserCheck_NotFound_OrFailed_IsNotSuccessful(DirectoryLookupStatus status, string expected)
    {
        await AdminLoginAsync();
        _directory.LookupResult = new DirectoryLookupResult(status, Detail: "detail");

        var (success, message) = await JsonResultAsync(await CheckUserAsync("kishi"));

        Assert.False(success);
        Assert.Contains(expected, message);
    }

    [Fact]
    public async Task UserCheck_WithAnInvalidName_DoesNotAskTheDirectory()
    {
        await AdminLoginAsync();

        var (success, message) = await JsonResultAsync(await CheckUserAsync("a,b"));

        Assert.False(success);
        Assert.Contains("正しくありません", message);
        Assert.Equal(0, _directory.LookupCalls);
    }

    [Fact]
    public async Task UserCheck_RequiresAnAdministrator()
    {
        var response = await NewClient().PostAsync(
            "/Users/CheckDirectoryUser", new FormUrlEncodedContent([new("email", "kishi")]), TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _directory.LookupCalls);
    }

    [Fact]
    public async Task CreatePage_HasTheLookupButton_OnlyForTheDirectoryMethod()
    {
        await AdminLoginAsync();

        var html = await _admin.GetStringAsync("/Users/Create", TestContext.Current.CancellationToken);

        Assert.Contains("data-check-url", html);
        Assert.Contains("data-auth-only=\"directory\"", html);
        Assert.Matches("data-auth-only=\"directory\"[^>]*hidden", html);
    }

    [Theory]
    [InlineData("/Users/Create")]
    [InlineData("/Settings/ActiveDirectory")]
    public async Task CheckButtons_SendOnlyFieldsThatExistInTheForm(string page)
    {
        // JS は data-check-fields の名前で form.elements を引く（大文字小文字を区別する）。
        // 名前が実際の入力欄と食い違うと、値が送られず「入力が正しくありません」になる。
        await AdminLoginAsync();
        var html = await _admin.GetStringAsync(page, TestContext.Current.CancellationToken);

        var fields = System.Text.RegularExpressions.Regex.Match(html, "data-check-fields=\"([^\"]+)\"").Groups[1].Value.Split(',');

        Assert.NotEmpty(fields);
        foreach (var field in fields)
        {
            Assert.Matches($"<(input|select)[^>]*name=\"{field}\"", html);
        }
    }

    /// <summary>偽の認証器の、設定確認・ユーザー検索の部分。</summary>
    private sealed partial class FakeDirectoryAuthenticator
    {
        public DirectoryCheckResult CheckResult { get; set; } = new(DirectoryCheckStatus.Success);

        public DirectoryLookupResult LookupResult { get; set; } = new(DirectoryLookupStatus.NotFound);

        public int CheckCalls { get; private set; }

        public int LookupCalls { get; private set; }

        public ActiveDirectorySettings? LastCheckedSettings { get; private set; }

        public string? LastBindPassword { get; private set; }

        public string? LastLookupUser { get; private set; }

        public Task<DirectoryCheckResult> CheckConnectionAsync(
            ActiveDirectorySettings settings, string? bindPassword, CancellationToken ct = default)
        {
            CheckCalls++;
            LastCheckedSettings = settings;
            LastBindPassword = bindPassword;
            return Task.FromResult(CheckResult);
        }

        public Task<DirectoryLookupResult> FindUserAsync(
            ActiveDirectorySettings settings, string? bindPassword, string userName, CancellationToken ct = default)
        {
            LookupCalls++;
            LastCheckedSettings = settings;
            LastBindPassword = bindPassword;
            LastLookupUser = userName;
            return Task.FromResult(LookupResult);
        }
    }
}
