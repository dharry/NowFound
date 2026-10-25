namespace NowFound.Domain;

/// <param name="Id">カルチャ名（RequestLocalization・Cookie・クレームで共通）。NowFound.Web/Resources の .resx と一致させる。</param>
/// <param name="NativeName">その言語自身での表記。切替 UI に表示する。</param>
public sealed record LanguageInfo(string Id, string NativeName);

/// <summary>画面の表示言語。</summary>
public static class AppLanguages
{
    public const string Default = "ja";

    public static readonly IReadOnlyList<LanguageInfo> All =
    [
        new("ja", "日本語"),
        new("en", "English"),
    ];

    public static bool IsValid(string? id) => All.Any(l => l.Id == id);

    /// <summary>未設定・不正な値（Cookie の改ざんなど）は既定の言語にする。</summary>
    public static string Normalize(string? id) => IsValid(id) ? id! : Default;
}
