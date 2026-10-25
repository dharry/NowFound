using System.Text.RegularExpressions;

namespace NowFound.Application.Indexing;

/// <summary>
/// 除外パターン（ワイルドカード * ? 対応、大文字小文字無視）の判定。
/// 末尾が / （または \）のパターンは、フォルダだけを対象にする（例: <c>__MACOSX/</c>）。
/// </summary>
public sealed class ExcludeMatcher
{
    private readonly (Regex Regex, bool DirectoryOnly)[] _patterns;

    public ExcludeMatcher(IEnumerable<string> patterns)
    {
        _patterns =
        [
            .. patterns
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Select(p =>
                {
                    var directoryOnly = p.Length > 1 && p[^1] is '/' or '\\';
                    var body = directoryOnly ? p[..^1] : p;
                    var regex = new Regex(
                        "^" + Regex.Escape(body).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                    return (regex, directoryOnly);
                }),
        ];
    }

    /// <summary>名前（ファイル名／フォルダ名）が除外対象かを返す。フォルダ限定のパターンは isDirectory のときだけ一致する。</summary>
    public bool IsExcluded(string name, bool isDirectory = false) =>
        _patterns.Any(p => (!p.DirectoryOnly || isDirectory) && p.Regex.IsMatch(name));
}
