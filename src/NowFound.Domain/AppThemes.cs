namespace NowFound.Domain;

/// <param name="Id">CSS の data-theme 値。wwwroot/css/themes.css のテーマ定義と一致させる。</param>
public sealed record ThemeInfo(string Id, bool IsDark);

/// <summary>ユーザーが選べる画面テーマ。表示名・説明は AccountResource の Theme.&lt;id&gt;.Name / Description。</summary>
public static class AppThemes
{
    public const string Default = "light";

    public static readonly IReadOnlyList<ThemeInfo> All =
    [
        new("light", false),
        new("dark", true),
        new("blue", false),
        new("solarized", true),
        new("desert", true),
        new("hybrid", true),
        new("slack", false),
        new("facebook", false),
        new("teams", false),
        new("discord", true),
    ];

    public static bool IsValid(string? id) => All.Any(t => t.Id == id);

    /// <summary>未設定・不正な値（Cookie の改ざんなど）は既定のテーマにする。</summary>
    public static string Normalize(string? id) => IsValid(id) ? id! : Default;
}
