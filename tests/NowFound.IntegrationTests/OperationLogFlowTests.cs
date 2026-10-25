using System.Net;

namespace NowFound.IntegrationTests;

public sealed class OperationLogFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Activities_AreRecordedAndVisibleToAdminOnly()
    {
        var anon = _factory.CreateBrowserLikeClient();
        await anon.PostFormAsync("/Account/Login", "/Account/Login", [new("Email", "ghost@example.com"), new("Password", "x")]);

        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        await admin.GetAsync("/?Conditions[0].Field=FileName&Conditions[0].Text=needle-query", TestContext.Current.CancellationToken);
        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "user@example.com"), new("Password", "Passw0rd!"), new("RequirePasswordChange", "false")]);

        var user = _factory.CreateBrowserLikeClient();
        await user.LoginAsync("user@example.com", "Passw0rd!");
        var denied = await user.GetAsync("/Logs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        // Cookie 認証が転送する AccessDenied ページへの到達を再現する。
        await user.GetAsync("/Account/AccessDenied?returnUrl=%2FLogs", TestContext.Current.CancellationToken);

        var html = await admin.GetStringAsync("/Logs", TestContext.Current.CancellationToken);
        Assert.Contains("ログイン失敗", html);
        Assert.Contains("ghost@example.com", html);
        Assert.Contains("needle-query", html);
        Assert.Contains("パスワード変更", html);
        Assert.Contains("アクセスエラー", html);
        Assert.Contains("user@example.com", html);

        // 操作・ユーザーのセルは、その値で絞り込むリンクになる（他の条件は引き継ぐ）。
        Assert.Contains("Operation=LoginFailed", html);
        Assert.Matches(@"User=user(%40|@)example\.com", html);

        var linked = await admin.GetStringAsync(
            "/Logs?From=2000-01-01&Operation=LoginFailed&User=ghost%40example.com", TestContext.Current.CancellationToken);
        Assert.Contains("<option value=\"LoginFailed\" selected", linked);
        Assert.Contains("value=\"ghost@example.com\"", linked);
        Assert.DoesNotContain("needle-query", linked);

        var filtered = await admin.GetStringAsync("/Logs?Operation=LoginFailed", TestContext.Current.CancellationToken);
        Assert.Contains("ghost@example.com", filtered);
        Assert.DoesNotContain("needle-query", filtered);

        var byUser = await admin.GetStringAsync("/Logs?User=ghost", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("needle-query", byUser);
    }

    [Fact]
    public async Task AdminChanges_AreRecorded()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        var root = Path.GetFullPath(Path.GetTempPath());

        await admin.PostFormAsync("/Users/Create", "/Users/Create",
            [new("Email", "newbie@example.com"), new("Password", "Passw0rd!")]);
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "AuditFolder"), new("Path", root)]);
        await admin.PostFormAsync("/Groups/Save", "/Groups/Create", [new("Name", "AuditGroup")]);
        await admin.PostFormAsync("/Settings", "/Settings", [new("PageSize", "77")]);
        await admin.PostFormAsync("/Folders/Delete?id=1", "/Folders", []);
        // id=1 は既定のグループ（削除できず、ログにも残らない）。AuditGroup は id=2。
        await admin.PostFormAsync("/Groups/Delete?id=1", "/Groups", []);
        await admin.PostFormAsync("/Groups/Delete?id=2", "/Groups", []);

        // 失敗した操作（相対パス）は記録されない。
        await admin.PostFormAsync("/Folders/Save", "/Folders/Create", [new("Name", "BadFolder"), new("Path", "relative")]);

        var html = await admin.GetStringAsync("/Logs?Operation=AdminChange", TestContext.Current.CancellationToken);
        Assert.Contains("ユーザー追加: newbie@example.com", html);
        Assert.Contains("検索対象フォルダ追加: AuditFolder", html);
        Assert.Contains("グループ追加: AuditGroup", html);
        Assert.Contains("ページサイズ既定値を変更: 77", html);
        Assert.Contains("検索対象フォルダ削除: AuditFolder", html);
        Assert.Contains("グループ削除: AuditGroup", html);
        Assert.DoesNotContain("グループ削除: デフォルトグループ", html);
        Assert.DoesNotContain("BadFolder", html);
    }

    [Fact]
    public async Task LogPageSize_IsSeparateFromSearchPageSize_AndPagesTheLog()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        // 検索結果のページサイズは大きいまま、操作ログだけ 1 件ずつにする。
        await admin.PostFormAsync("/Settings", "/Settings", [new("PageSize", "500"), new("LogPageSize", "1")]);
        var settings = await admin.GetStringAsync("/Settings", TestContext.Current.CancellationToken);
        Assert.Contains("操作ログのページサイズ", settings);

        var first = await admin.GetStringAsync("/Logs", TestContext.Current.CancellationToken);
        Assert.Contains("aria-label=\"ページ移動\"", first);
        Assert.Contains("次へ", first);

        var second = await admin.GetStringAsync("/Logs?Page=2", TestContext.Current.CancellationToken);
        Assert.Contains("前へ", second);
    }

    [Fact]
    public async Task ClearLink_ReturnsToTheUnfilteredLog()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var html = await admin.GetStringAsync("/Logs?Operation=Login&User=zzz", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/Logs\"", html);
        Assert.Contains("クリア", html);
    }

    [Fact]
    public async Task Logout_IsRecorded()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();
        await admin.PostFormAsync("/Account/Logout", "/Settings", []);
        await admin.LoginAsync("admin@nowfound.invalid", "NewPassw0rd!");

        var html = await admin.GetStringAsync("/Logs?Operation=Logout", TestContext.Current.CancellationToken);
        Assert.Contains("ログアウト", html);
        Assert.Contains("admin@nowfound.invalid", html);
    }
}
