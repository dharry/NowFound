using System.Text.RegularExpressions;

namespace NowFound.Web.Security;

/// <summary>
/// サブディレクトリ方式で公開するときのパス（設定キー <c>PathBase</c>）。
/// 空ならドメイン方式（ルート直下）。例: <c>/nowfound</c>。
/// </summary>
public static partial class PathBaseSettings
{
    public const string ConfigurationKey = "PathBase";

    [GeneratedRegex(@"^(/[A-Za-z0-9][A-Za-z0-9._~-]*)+$")]
    private static partial Regex ValidPattern { get; }

    /// <summary>
    /// 設定値を正規化する。先頭に <c>/</c> を補い、末尾の <c>/</c> を除く。空・空白・<c>/</c> のみは <c>null</c>（ドメイン方式）。
    /// URL として安全でない文字を含む場合は <see cref="ArgumentException"/>。
    /// </summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var normalized = trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
        if (!ValidPattern.IsMatch(normalized))
        {
            throw new ArgumentException(
                $"{ConfigurationKey} には英数字・'.'・'_'・'~'・'-' からなるパス（例: /nowfound）を指定してください: '{value}'",
                nameof(value));
        }

        return normalized;
    }

    /// <summary>アプリ内の絶対パス（<c>/Account/Login</c> など）に、現在の PathBase を付けた URL を返す。</summary>
    public static string Prefixed(HttpRequest request, string appPath) => $"{request.PathBase}{appPath}";
}
