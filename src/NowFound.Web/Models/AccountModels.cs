namespace NowFound.Web.Models;

public class LoginViewModel
{
    /// <summary>ログインのタブ（<see cref="NowFound.Domain.AuthSources"/>）。</summary>
    public string AuthSource { get; set; } = NowFound.Domain.AuthSources.Local;

    /// <summary>標準タブのメールアドレス。</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>ActiveDirectory タブのユーザー名（sAMAccountName。ドメイン名とバックスラッシュを前置した形式も可）。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>ActiveDirectory 認証が使える状態か。使えるときだけログイン画面にタブを出す（表示用）。</summary>
    public bool DirectoryEnabled { get; set; }

    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class ChangePasswordViewModel
{
    public string CurrentPassword { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;

    public string ConfirmPassword { get; set; } = string.Empty;
}

public class CreateUserViewModel
{
    /// <summary>認証方式（<see cref="NowFound.Domain.AuthSources"/>）。</summary>
    public string AuthSource { get; set; } = NowFound.Domain.AuthSources.Local;

    /// <summary>アカウント。標準ユーザーはメールアドレス、ActiveDirectory ユーザーは sAMAccountName。</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>連絡先のメールアドレス（ActiveDirectory ユーザーのみ。任意）。標準ユーザーはアカウントを使う。</summary>
    public string? MailAddress { get; set; }

    /// <summary>説明（任意）。</summary>
    public string? Description { get; set; }

    public string Password { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    public bool RequirePasswordChange { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    /// <summary>有効期限（この日の終わりまで有効）。空は無期限。</summary>
    public DateOnly? ExpiresOn { get; set; }
}

public class UserEditViewModel
{
    public string Id { get; set; } = string.Empty;

    /// <summary>アカウント（表示のみ）。</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>連絡先のメールアドレス。標準ユーザーは表示のみ（アカウントと同じ）、ActiveDirectory ユーザーは編集できる。</summary>
    public string? MailAddress { get; set; }

    /// <summary>説明（任意）。</summary>
    public string? Description { get; set; }

    public bool IsAdmin { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateOnly? ExpiresOn { get; set; }

    public bool IsLockedOut { get; set; }

    public string AuthSource { get; set; } = NowFound.Domain.AuthSources.Local;

    /// <summary>ログイン中の本人（本人は削除できない）。</summary>
    public bool IsSelf { get; set; }
}

/// <summary>アカウントポリシー（システム設定）の編集。</summary>
public class AccountPolicyViewModel
{
    public int? PasswordMinLength { get; set; }

    public bool RequireComplexity { get; set; }

    public int? MaxFailedAttempts { get; set; }

    /// <summary>パスワードの有効日数。0 は無期限。</summary>
    public int? PasswordMaxAgeDays { get; set; }

    /// <summary>再利用を禁止する直近のパスワードの数。0 は禁止しない。</summary>
    public int? PasswordHistoryCount { get; set; }
}

/// <summary>ActiveDirectory 認証（システム設定）の編集。</summary>
public class ActiveDirectoryViewModel
{
    public bool Enabled { get; set; }

    public string? Server { get; set; }

    public int? Port { get; set; }

    public NowFound.Application.Users.DirectorySecurity Security { get; set; }

    public string? Domain { get; set; }

    /// <summary>ユーザーの存在確認に使う確認用アカウント（任意）。</summary>
    public string? BindUser { get; set; }

    /// <summary>確認用アカウントのパスワード。空なら保存済みの値を変えない。画面には再表示しない。</summary>
    public string? BindPassword { get; set; }

    /// <summary>存在確認で検索する起点の DN。空ならドメインから求める。</summary>
    public string? SearchBase { get; set; }

    /// <summary>確認用アカウントのパスワードが保存済みか（表示用）。</summary>
    public bool HasBindPassword { get; set; }
}
