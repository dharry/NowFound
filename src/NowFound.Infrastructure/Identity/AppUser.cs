using Microsoft.AspNetCore.Identity;

namespace NowFound.Infrastructure.Identity;

public class AppUser : IdentityUser
{
    /// <summary>
    /// 認証方式（<see cref="NowFound.Domain.AuthSources"/>）。ActiveDirectory のユーザーはパスワードを持たず、
    /// <see cref="IdentityUser.UserName"/> は <c>ad:</c> + sAMAccountName、<see cref="IdentityUser.Email"/> には表示と検索のため
    /// sAMAccountName を入れる（メールアドレスではない）。
    /// </summary>
    public string AuthSource { get; set; } = NowFound.Domain.AuthSources.Local;

    /// <summary>
    /// 連絡先のメールアドレス（アカウントとは別）。標準ユーザーはアカウントがメールアドレスなので、アカウントと同じ値になる
    /// （未設定の古いユーザーは読み出し側でアカウントを使う）。ActiveDirectory のユーザーは任意で、未設定なら null。
    /// </summary>
    public string? MailAddress { get; set; }

    /// <summary>管理者が付ける説明（任意）。未設定なら null。</summary>
    public string? Description { get; set; }

    /// <summary>ActiveDirectory のユーザーか。</summary>
    public bool IsDirectoryUser => AuthSource == NowFound.Domain.AuthSources.ActiveDirectory;

    /// <summary>次回ログイン時にパスワード変更を要求する。</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>ユーザー個別のページサイズ。null の場合は管理者設定の既定値。</summary>
    public int? PageSize { get; set; }

    /// <summary>画面テーマの ID（AppThemes）。null の場合は既定のテーマ。</summary>
    public string? Theme { get; set; }

    /// <summary>表示言語（AppLanguages）。null の場合は Cookie・ブラウザの言語・既定の順で決まる。</summary>
    public string? Language { get; set; }

    /// <summary>ブックマーク一覧のページサイズ。null の場合は既定値（SystemSetting.DefaultListPageSize）。</summary>
    public int? BookmarkPageSize { get; set; }

    /// <summary>アカウントが有効か。false の間はログインできず、ログイン中のセッションも無効になる。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>有効期限（サーバーのローカル日付。この日の終わりまで有効）。null は無期限。</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>パスワードを最後に設定した日時（UTC）。パスワードの有効期間の起点。不明（null）の場合は期限切れにしない。</summary>
    public DateTime? PasswordChangedAt { get; set; }
}
