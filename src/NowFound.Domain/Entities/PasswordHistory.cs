namespace NowFound.Domain.Entities;

/// <summary>ユーザーが過去に設定したパスワードのハッシュ（再利用禁止の判定用）。平文は保持しない。</summary>
public class PasswordHistory
{
    public long Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>UTC。</summary>
    public DateTime CreatedAt { get; set; }
}
