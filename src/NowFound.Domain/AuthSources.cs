namespace NowFound.Domain;

/// <summary>ユーザーの認証方式。</summary>
public static class AuthSources
{
    /// <summary>標準（メールアドレスとローカルに保存したパスワード）。</summary>
    public const string Local = "Local";

    /// <summary>ActiveDirectory（パスワードは AD で検証し、NowFound は保存しない）。</summary>
    public const string ActiveDirectory = "ActiveDirectory";

    /// <summary>AD ユーザーの <c>UserName</c> に付ける接頭辞（標準ユーザーのメールアドレスと衝突させない）。</summary>
    public const string ActiveDirectoryUserNamePrefix = "ad:";

    public static bool IsValid(string? source) => source is Local or ActiveDirectory;

    /// <summary>未設定（既存ユーザー）は標準として扱う。</summary>
    public static string Normalize(string? source) => source == ActiveDirectory ? ActiveDirectory : Local;
}
