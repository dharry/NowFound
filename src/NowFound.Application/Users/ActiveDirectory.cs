using System.Text.RegularExpressions;

namespace NowFound.Application.Users;

/// <summary>接続の保護方式。</summary>
public enum DirectorySecurity
{
    /// <summary>暗号化しない（389）。検証用。</summary>
    None = 0,

    /// <summary>LDAPS（636）。</summary>
    Ssl = 1,

    /// <summary>StartTLS（389 で接続後に暗号化）。</summary>
    StartTls = 2,
}

/// <summary>
/// ActiveDirectory 認証の設定（システム設定）。ユーザーは <c>sAMAccountName@Domain</c>（UPN）でバインドして検証する。
/// </summary>
/// <param name="Domain">UPN のサフィックス（例: corp.example.com）。</param>
/// <param name="BindUser">
/// ユーザーの存在確認に使う「確認用アカウント」（読み取り専用）。ユーザー名だけならドメインを補って UPN にする。
/// UPN（user@domain）や DN も指定できる。空なら存在確認は使えない。パスワードはこの型に含めず、別に暗号化して保存する。
/// </param>
/// <param name="SearchBase">存在確認で検索する起点の DN。空ならドメインから求める（corp.example.com → DC=corp,DC=example,DC=com）。</param>
public sealed record ActiveDirectorySettings(
    bool Enabled, string Server, int Port, DirectorySecurity Security, string Domain, string BindUser = "", string SearchBase = "")
{
    public const int BindUserMaxLength = 256;
    public const int SearchBaseMaxLength = 512;

    public const int PortMin = 1;
    public const int PortMax = 65535;
    public const int ServerMaxLength = 255;
    public const int DomainMaxLength = 255;

    public static ActiveDirectorySettings Default { get; } = new(false, string.Empty, 389, DirectorySecurity.None, string.Empty);

    /// <summary>セキュリティの方式に応じた既定のポート。</summary>
    public static int DefaultPort(DirectorySecurity security) => security == DirectorySecurity.Ssl ? 636 : 389;

    /// <summary>ログインに必要な項目が揃っているか。</summary>
    public bool IsConfigured => Enabled && Server.Length > 0 && Domain.Length > 0;
}

public static partial class ActiveDirectoryRules
{
    /// <summary>sAMAccountName の最大長。</summary>
    public const int UserNameMaxLength = 20;

    // sAMAccountName に使えない文字（" / \ [ ] : ; | = , + * ? < > @）と空白・制御文字を除く。
    [GeneratedRegex(@"^[^\x00-\x20""/\\\[\]:;|=,+*?<>@]+$")]
    private static partial Regex UserNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();

    public static bool IsValidUserName(string? userName) =>
        !string.IsNullOrEmpty(userName) && userName.Length <= UserNameMaxLength && UserNamePattern().IsMatch(userName);

    /// <summary>ホスト名・FQDN・IP アドレスとして妥当か。</summary>
    public static bool IsValidHost(string? host) =>
        !string.IsNullOrEmpty(host) && host.Length <= ActiveDirectorySettings.ServerMaxLength && HostPattern().IsMatch(host);

    /// <summary>
    /// ログイン画面の入力から sAMAccountName を取り出す。<c>DOMAIN\user</c> 形式なら <c>user</c> 部分を使う。
    /// 妥当でなければ null。
    /// </summary>
    public static string? NormalizeUserName(string? input)
    {
        var name = input?.Trim() ?? string.Empty;
        var slash = name.LastIndexOf('\\');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return IsValidUserName(name) ? name : null;
    }

    /// <summary>制御文字を含まない、長さの範囲内の文字列か。</summary>
    private static bool IsPlainText(string text, int maxLength) => text.Length <= maxLength && !text.Any(char.IsControl);

    /// <summary>DN として最低限の形か（属性=値を含む）。厳密な構文は AD に任せる。</summary>
    public static bool IsValidSearchBase(string? searchBase) =>
        !string.IsNullOrEmpty(searchBase) && searchBase.Contains('=') && IsPlainText(searchBase, ActiveDirectorySettings.SearchBaseMaxLength);

    /// <summary>検索の起点の DN。未指定ならドメインから求める。ドメインも無ければ空。</summary>
    public static string EffectiveSearchBase(ActiveDirectorySettings settings) =>
        settings.SearchBase.Length > 0
            ? settings.SearchBase
            : string.Join(',', settings.Domain.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part => "DC=" + part));

    /// <summary>確認用アカウントのバインド名。ユーザー名だけなら <c>user@Domain</c> にする。</summary>
    public static string EffectiveBindName(ActiveDirectorySettings settings) =>
        settings.BindUser.IndexOfAny(['@', '=', '\\']) >= 0 || settings.Domain.Length == 0
            ? settings.BindUser
            : $"{settings.BindUser}@{settings.Domain}";

    /// <summary>LDAP 検索フィルターの値として使えるように、特殊文字をエスケープする（RFC 4515）。</summary>
    public static string EscapeFilterValue(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => "\\5c",
                '*' => "\\2a",
                '(' => "\\28",
                ')' => "\\29",
                '\0' => "\\00",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    public static OperationResult Validate(ActiveDirectorySettings settings)
    {
        if (settings.BindUser.Length > 0 && !IsPlainText(settings.BindUser, ActiveDirectorySettings.BindUserMaxLength))
        {
            return OperationResult.Failure("Error.Ad.BindUserInvalid");
        }

        if (settings.SearchBase.Length > 0 && !IsValidSearchBase(settings.SearchBase))
        {
            return OperationResult.Failure("Error.Ad.SearchBaseInvalid");
        }

        // 無効のまま保存するときは、未入力の項目があっても構わない。入力されている項目だけは検証する。
        if (settings.Server.Length > 0 && !IsValidHost(settings.Server))
        {
            return OperationResult.Failure("Error.Ad.ServerInvalid");
        }

        if (settings.Domain.Length > 0 && !IsValidHost(settings.Domain))
        {
            return OperationResult.Failure("Error.Ad.DomainInvalid");
        }

        if (settings.Port is < ActiveDirectorySettings.PortMin or > ActiveDirectorySettings.PortMax)
        {
            return OperationResult.Failure("Error.Ad.PortRange", ActiveDirectorySettings.PortMin, ActiveDirectorySettings.PortMax);
        }

        if (!Enum.IsDefined(settings.Security))
        {
            return OperationResult.Failure("Error.Ad.SecurityInvalid");
        }

        if (settings.Enabled && (settings.Server.Length == 0 || settings.Domain.Length == 0))
        {
            return OperationResult.Failure("Error.Ad.Incomplete");
        }

        return OperationResult.Success;
    }
}

public enum DirectoryAuthStatus
{
    /// <summary>認証に成功した。</summary>
    Success,

    /// <summary>ユーザー名またはパスワードが違う（AD に拒否された）。</summary>
    InvalidCredentials,

    /// <summary>サーバーに接続できない・設定が不完全など、認証の可否を判断できなかった。</summary>
    Unavailable,
}

public enum DirectoryCheckStatus
{
    Success,

    /// <summary>サーバーが未入力など、確認できる状態ではない。</summary>
    NotConfigured,

    /// <summary>接続できない（名前解決・ポート・TLS・証明書・タイムアウトなど）。</summary>
    ConnectFailed,

    /// <summary>接続できたが、確認用アカウントのバインドが拒否された。</summary>
    BindRejected,
}

/// <param name="BindTested">確認用アカウントでのバインドまで確認したか。</param>
/// <param name="Detail">失敗の原因の要約（管理者向け。パスワードは含まない）。</param>
public sealed record DirectoryCheckResult(DirectoryCheckStatus Status, bool BindTested = false, string? Detail = null)
{
    public bool Succeeded => Status == DirectoryCheckStatus.Success;
}

public enum DirectoryLookupStatus
{
    Found,
    NotFound,

    /// <summary>確認用アカウント（またはそのパスワード）が未設定。</summary>
    NotConfigured,

    ConnectFailed,
    BindRejected,

    /// <summary>検索そのものに失敗した（検索起点の DN が違う、権限がないなど）。</summary>
    SearchFailed,
}

/// <param name="DisplayName">AD の表示名（無ければ null）。</param>
/// <param name="AccountDisabled">AD 側でアカウントが無効化されているか。</param>
public sealed record DirectoryLookupResult(
    DirectoryLookupStatus Status, string? DisplayName = null, bool AccountDisabled = false, string? Detail = null);

/// <summary>ActiveDirectory へのパスワード検証と、設定の確認・ユーザーの存在確認。</summary>
public interface IDirectoryAuthenticator
{
    /// <summary>
    /// 接続（と TLS）を確認する。確認用アカウントが指定されていれば、そのバインドまで確認する。
    /// 画面で入力中の（保存前の）設定を渡せる。
    /// </summary>
    Task<DirectoryCheckResult> CheckConnectionAsync(
        ActiveDirectorySettings settings, string? bindPassword, CancellationToken ct = default);

    /// <summary>確認用アカウントで検索し、sAMAccountName のユーザーが AD にいるかを調べる。</summary>
    Task<DirectoryLookupResult> FindUserAsync(
        ActiveDirectorySettings settings, string? bindPassword, string userName, CancellationToken ct = default);

    /// <param name="userName">sAMAccountName（<see cref="ActiveDirectoryRules.NormalizeUserName"/> 済み）。</param>
    Task<DirectoryAuthStatus> AuthenticateAsync(
        ActiveDirectorySettings settings, string userName, string password, CancellationToken ct = default);
}

public interface IActiveDirectorySettingsService
{
    Task<ActiveDirectorySettings> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// 設定を保存する。<paramref name="bindPassword"/> が空なら保存済みのパスワードを変えない。
    /// 確認用アカウントが空なら、保存済みのパスワードも削除する。
    /// <paramref name="requireBindPassword"/> が false なら、確認用アカウントを指定してもパスワード未保存のまま保存できる（設定の取り込み用）。
    /// </summary>
    Task<OperationResult> SetAsync(
        ActiveDirectorySettings settings, string? bindPassword = null, CancellationToken ct = default, bool requireBindPassword = true);

    /// <summary>確認用アカウントのパスワードが保存されているか。</summary>
    Task<bool> HasBindPasswordAsync(CancellationToken ct = default);

    /// <summary>保存済みの確認用アカウントのパスワード（復号済み）。無い、または復号できなければ null。</summary>
    Task<string?> GetBindPasswordAsync(CancellationToken ct = default);
}

/// <summary>標準ユーザーのメールアドレスの検証。ActiveDirectory ユーザーの sAMAccountName（'@' を含まない）と区別する。</summary>
public static class EmailRules
{
    private static readonly System.ComponentModel.DataAnnotations.EmailAddressAttribute Validator = new();

    public static bool IsValid(string? email) =>
        !string.IsNullOrWhiteSpace(email) && email.Length <= 256 && Validator.IsValid(email);
}
