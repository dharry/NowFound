namespace NowFound.Domain;

/// <summary>アプリケーションのロール名。</summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string User = "User";

    public static readonly IReadOnlyList<string> All = [Admin, User];
}
