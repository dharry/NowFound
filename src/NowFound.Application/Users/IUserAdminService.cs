using NowFound.Domain;

namespace NowFound.Application.Users;

/// <param name="IsEnabled">アカウントが有効か。</param>
/// <param name="ExpiresOn">有効期限（この日の終わりまで有効）。null は無期限。</param>
/// <param name="IsLockedOut">ログイン失敗によるロック中か。</param>
/// <param name="MailAddress">連絡先のメールアドレス（<see cref="UserSummary.Email"/> はアカウント）。標準ユーザーはアカウントと同じ。</param>
/// <param name="Description">管理者が付けた説明（任意）。</param>
public sealed record UserSummary(
    string Id,
    string Email,
    bool IsAdmin,
    bool MustChangePassword,
    bool IsEnabled = true,
    DateOnly? ExpiresOn = null,
    bool IsLockedOut = false,
    string AuthSource = AuthSources.Local,
    string? MailAddress = null,
    string? Description = null);

/// <summary>管理者が編集できるユーザーの項目。</summary>
public sealed class UserEdit
{
    public string Id { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateOnly? ExpiresOn { get; set; }

    public bool IsLockedOut { get; set; }

    /// <summary>認証方式（<see cref="AuthSources"/>）。作成後は変更できない。</summary>
    public string AuthSource { get; set; } = AuthSources.Local;

    /// <summary>
    /// 連絡先のメールアドレス。標準ユーザーはアカウント（<see cref="Email"/>）と同じで変更できない。
    /// ActiveDirectory ユーザーは任意（空なら未設定）。
    /// </summary>
    public string? MailAddress { get; set; }

    /// <summary>説明（任意）。空なら未設定。</summary>
    public string? Description { get; set; }
}

/// <param name="MailAddress">
/// 連絡先のメールアドレス。ActiveDirectory ユーザーのみ任意で指定する（標準ユーザーはアカウントのメールアドレスを使うため無視する）。
/// </param>
/// <param name="Email">
/// 標準ユーザーはメールアドレス。ActiveDirectory ユーザーは sAMAccountName（このとき <paramref name="Password"/> と
/// <paramref name="RequirePasswordChange"/> は使わない）。
/// </param>
public sealed record CreateUserRequest(
    string Email,
    string Password,
    bool IsAdmin,
    bool RequirePasswordChange,
    bool IsEnabled = true,
    DateOnly? ExpiresOn = null,
    string AuthSource = AuthSources.Local,
    string? MailAddress = null,
    string? Description = null);

public sealed record OperationResult(bool Succeeded, IReadOnlyList<LocalizedMessage> Errors)
{
    public static OperationResult Success { get; } = new(true, []);

    /// <summary>key はリソースのキー（NowFound.Web/Resources/Messages）。args は {0} {1} … に入る。</summary>
    public static OperationResult Failure(string key, params object?[] args) => new(false, [new LocalizedMessage(key, args)]);

    public static OperationResult Failure(IEnumerable<LocalizedMessage> errors) => new(false, [.. errors]);
}

/// <summary>管理者によるユーザー管理。</summary>
public interface IUserAdminService
{
    Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct = default);

    /// <summary>ユーザー管理の一覧用。検索・並べ替え（既定はメールアドレス順）をして 1 ページ分。</summary>
    Task<PagedResult<UserSummary>> ListPageAsync(
        int page, int pageSize, UserListQuery? query = null, CancellationToken ct = default);

    /// <summary>ユーザー個別のブックマーク一覧のページサイズ。未設定なら null。</summary>
    Task<int?> GetBookmarkPageSizeAsync(string userId, CancellationToken ct = default);

    Task<OperationResult> SetBookmarkPageSizeAsync(string userId, int? pageSize, CancellationToken ct = default);

    Task<OperationResult> CreateAsync(CreateUserRequest request);

    Task<UserEdit?> GetAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// ユーザーの権限・有効/無効・有効期限を更新する。<paramref name="actingUserId"/> 自身の無効化・期限切れ・権限の変更と、
    /// 有効な管理者が 0 人になる変更は拒否する。<paramref name="today"/> はサーバーのローカル日付。
    /// </summary>
    Task<OperationResult> UpdateAsync(UserEdit edit, string actingUserId, DateOnly today, CancellationToken ct = default);

    /// <summary>
    /// ユーザーを削除する（所属グループ・ブックマーク・パスワード履歴も削除。操作ログは残す）。
    /// 自分自身と、他に有効な管理者がいなくなる管理者は削除できない。
    /// </summary>
    Task<OperationResult> DeleteAsync(string userId, string actingUserId, DateOnly today, CancellationToken ct = default);

    /// <summary>ログイン失敗によるロックを解除する。</summary>
    Task<OperationResult> UnlockAsync(string userId, CancellationToken ct = default);

    Task<OperationResult> ChangePasswordAsync(string userId, string currentPassword, string newPassword);

    /// <summary>
    /// 管理者が標準ユーザーのパスワードを再設定する（ユーザーがパスワードを忘れたとき）。アカウントポリシー（最小文字数・複雑性・履歴）を適用し、
    /// ロックを解除し、既存のセッションを無効にする。<paramref name="requireChange"/> が true なら次回ログイン時に変更を求める。
    /// ActiveDirectory ユーザーと、<paramref name="actingUserId"/> 自身は対象外。
    /// </summary>
    Task<OperationResult> ResetPasswordAsync(
        string userId, string newPassword, bool requireChange, string actingUserId, CancellationToken ct = default);

    /// <summary>ユーザー個別のページサイズ。未設定なら null。</summary>
    Task<int?> GetPageSizeAsync(string userId, CancellationToken ct = default);

    /// <summary>null を渡すと個別設定を解除し、管理者の既定値に戻す。</summary>
    Task<OperationResult> SetPageSizeAsync(string userId, int? pageSize, CancellationToken ct = default);

    /// <summary>ユーザーの画面テーマ。未設定なら null（既定のテーマ）。</summary>
    /// <summary>ユーザーの表示言語（AppLanguages）。未設定なら null。</summary>
    Task<string?> GetLanguageAsync(string userId, CancellationToken ct = default);

    Task<OperationResult> SetLanguageAsync(string userId, string language, CancellationToken ct = default);

    Task<string?> GetThemeAsync(string userId, CancellationToken ct = default);

    Task<OperationResult> SetThemeAsync(string userId, string theme, CancellationToken ct = default);
}
