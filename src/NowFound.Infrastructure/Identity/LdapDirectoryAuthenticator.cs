using Microsoft.Extensions.Logging;
using NowFound.Application.Users;
using Novell.Directory.Ldap;

namespace NowFound.Infrastructure.Identity;

/// <summary>
/// <c>sAMAccountName@Domain</c>（UPN）でバインドして、ActiveDirectory のパスワードを検証する。
/// ログインの検証にはバインド用のアカウントを使わない。ユーザーの存在確認だけ、設定された確認用アカウントで検索する。
/// </summary>
public sealed class LdapDirectoryAuthenticator(ILogger<LdapDirectoryAuthenticator> logger) : IDirectoryAuthenticator
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private const int MaxDetailLength = 400;

    public async Task<DirectoryAuthStatus> AuthenticateAsync(
        ActiveDirectorySettings settings, string userName, string password, CancellationToken ct = default)
    {
        // 空のパスワードでの単純バインドは「匿名バインド」として成功してしまうため、必ず拒否する。
        if (string.IsNullOrEmpty(password) || !ActiveDirectoryRules.IsValidUserName(userName))
        {
            return DirectoryAuthStatus.InvalidCredentials;
        }

        if (!settings.IsConfigured)
        {
            return DirectoryAuthStatus.Unavailable;
        }

        try
        {
            using var timeout = CreateTimeout(ct);
            using var connection = await ConnectAsync(settings, timeout.Token);
            await connection.BindAsync($"{userName}@{settings.Domain}", password).WaitAsync(timeout.Token);
            return connection.Bound ? DirectoryAuthStatus.Success : DirectoryAuthStatus.InvalidCredentials;
        }
        catch (LdapException ex) when (ex.ResultCode == LdapException.InvalidCredentials)
        {
            return DirectoryAuthStatus.InvalidCredentials;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // パスワードはログに出さない。接続できない原因（証明書・名前解決・タイムアウトなど）は管理者が追えるよう記録する。
            logger.LogWarning(ex, "ActiveDirectory への接続または認証に失敗しました（{Server}:{Port}）。", settings.Server, settings.Port);
            return DirectoryAuthStatus.Unavailable;
        }
    }

    public async Task<DirectoryCheckResult> CheckConnectionAsync(
        ActiveDirectorySettings settings, string? bindPassword, CancellationToken ct = default)
    {
        if (settings.Server.Length == 0)
        {
            return new DirectoryCheckResult(DirectoryCheckStatus.NotConfigured);
        }

        var bindTested = false;
        try
        {
            using var timeout = CreateTimeout(ct);
            using var connection = await ConnectAsync(settings, timeout.Token);
            if (settings.BindUser.Length > 0 && !string.IsNullOrEmpty(bindPassword))
            {
                bindTested = true;
                await connection.BindAsync(ActiveDirectoryRules.EffectiveBindName(settings), bindPassword).WaitAsync(timeout.Token);
            }

            return new DirectoryCheckResult(DirectoryCheckStatus.Success, bindTested);
        }
        catch (LdapException ex) when (bindTested && ex.ResultCode == LdapException.InvalidCredentials)
        {
            return new DirectoryCheckResult(DirectoryCheckStatus.BindRejected, true, Summarize(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ActiveDirectory の接続確認に失敗しました（{Server}:{Port}）。", settings.Server, settings.Port);
            return new DirectoryCheckResult(DirectoryCheckStatus.ConnectFailed, bindTested, Summarize(ex));
        }
    }

    public async Task<DirectoryLookupResult> FindUserAsync(
        ActiveDirectorySettings settings, string? bindPassword, string userName, CancellationToken ct = default)
    {
        var searchBase = ActiveDirectoryRules.EffectiveSearchBase(settings);
        if (settings.Server.Length == 0 || settings.BindUser.Length == 0 || string.IsNullOrEmpty(bindPassword) || searchBase.Length == 0)
        {
            return new DirectoryLookupResult(DirectoryLookupStatus.NotConfigured);
        }

        // ユーザー名はフィルターの一部になるため、特殊文字を必ずエスケープする（LDAP インジェクションの防止）。
        var filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={ActiveDirectoryRules.EscapeFilterValue(userName)}))";
        var bound = false;
        try
        {
            using var timeout = CreateTimeout(ct);
            using var connection = await ConnectAsync(settings, timeout.Token);
            await connection.BindAsync(ActiveDirectoryRules.EffectiveBindName(settings), bindPassword).WaitAsync(timeout.Token);
            bound = true;

            var results = await connection.SearchAsync(
                searchBase, LdapConnection.ScopeSub, filter, ["displayName", "userAccountControl"], false).WaitAsync(timeout.Token);
            while (await results.HasMoreAsync().WaitAsync(timeout.Token))
            {
                LdapEntry entry;
                try
                {
                    entry = await results.NextAsync().WaitAsync(timeout.Token);
                }
                catch (LdapReferralException)
                {
                    // 別のドメインへの参照は追わない（このサーバーの検索起点の中だけを調べる）。
                    continue;
                }

                var attributes = entry.GetAttributeSet();
                var displayName = attributes.TryGetValue("displayName", out var nameAttribute) ? nameAttribute.StringValue : null;
                var disabled = attributes.TryGetValue("userAccountControl", out var controlAttribute)
                    && int.TryParse(controlAttribute.StringValue, out var control)
                    && (control & 0x2) != 0;
                return new DirectoryLookupResult(DirectoryLookupStatus.Found, displayName, disabled);
            }

            return new DirectoryLookupResult(DirectoryLookupStatus.NotFound);
        }
        catch (LdapException ex) when (!bound && ex.ResultCode == LdapException.InvalidCredentials)
        {
            return new DirectoryLookupResult(DirectoryLookupStatus.BindRejected, Detail: Summarize(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ActiveDirectory のユーザー検索に失敗しました（{Server}:{Port}）。", settings.Server, settings.Port);
            return new DirectoryLookupResult(
                bound ? DirectoryLookupStatus.SearchFailed : DirectoryLookupStatus.ConnectFailed, Detail: Summarize(ex));
        }
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        return timeout;
    }

    /// <summary>接続して（Ssl は LDAPS、StartTls は接続後に暗号化へ切り替えて）、バインド前の接続を返す。</summary>
    private static async Task<LdapConnection> ConnectAsync(ActiveDirectorySettings settings, CancellationToken ct)
    {
        var options = new LdapConnectionOptions();
        if (settings.Security == DirectorySecurity.Ssl)
        {
            options.UseSsl();
        }

        var connection = new LdapConnection(options) { ConnectionTimeout = (int)ConnectTimeout.TotalMilliseconds };
        try
        {
            await connection.ConnectAsync(settings.Server, settings.Port).WaitAsync(ct);
            if (settings.Security == DirectorySecurity.StartTls)
            {
                await connection.StartTlsAsync().WaitAsync(ct);
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>管理者が原因を追えるよう、例外の連鎖のメッセージを短くまとめる。パスワードは含まれない。</summary>
    private static string Summarize(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (message.Length > 0 && !messages.Contains(message))
            {
                messages.Add(message);
            }
        }

        var text = string.Join(" / ", messages);
        return text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";
    }
}
