namespace NowFound.Domain.Entities;

public enum OperationType
{
    Login,
    LoginFailed,
    Logout,
    AccessDenied,
    Search,
    PasswordChange,
    AdminChange,
    Export,
}

/// <summary>操作ログ（ope.db）。</summary>
public class OperationLog
{
    public long Id { get; set; }

    /// <summary>UTC。</summary>
    public DateTime Timestamp { get; set; }
    public OperationType Operation { get; set; }

    /// <summary>未認証の操作（ログイン失敗など）では null。</summary>
    public string? UserId { get; set; }

    /// <summary>ユーザーのメールアドレス。ログイン失敗時は入力された値。</summary>
    public string UserEmail { get; set; } = string.Empty;

    /// <summary>旧形式（文章を直接保存していた頃）のログの内容。新しいログは DetailKey / DetailArgs を使う。</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>内容のリソースキー。表示時に管理者の言語へ翻訳する。旧形式のログは null。</summary>
    public string? DetailKey { get; set; }

    /// <summary>DetailKey の引数（JSON の文字列配列）。</summary>
    public string? DetailArgs { get; set; }
    public string? IpAddress { get; set; }
}
