namespace NowFound.Application.Users;

/// <summary>
/// アカウント状態の軽量スナップショット（キャッシュ用）。
/// </summary>
public sealed record AccountStateSnapshot(
    bool Exists,
    bool IsEnabled,
    DateOnly? ExpiresOn,
    DateTime? PasswordChangedAt,
    string? SecurityStamp);

/// <summary>
/// 認証済みリクエストごとのアカウント状態確認をキャッシュし、DB負荷を抑える。
/// ユーザー情報の変更・削除時にキャッシュを即座に無効化する。
/// </summary>
public interface IAccountStatusCache
{
    ValueTask<AccountStateSnapshot> GetOrLoadAsync(
        string userId,
        Func<string, CancellationToken, Task<AccountStateSnapshot>> factory,
        CancellationToken ct = default);

    void Invalidate(string userId);
}
