namespace NowFound.Application.Settings;

/// <summary>
/// 除外パターンの既定値と検証。パターンは名前（ファイル名／フォルダ名）に対する * ? のワイルドカードで、
/// 末尾が / のものはフォルダだけが対象になる。コメント行は無い（Emacs の <c>#*#</c> が # で始まるため）。
/// </summary>
public static class ExcludePatternRules
{
    public const int MaxPatterns = 100;
    public const int MaxPatternLength = 100;

    /// <summary>1 つの設定値に収まる全体の長さ（改行区切りで連結した長さ）。</summary>
    public const int MaxTotalLength = 1000;

    /// <summary>組み込みの既定値（macOS / Windows / Office / Vim / Emacs / Unix・NFS・Samba の一時・管理ファイル）。</summary>
    public static readonly IReadOnlyList<string> BuiltIn =
    [
        // macOS
        ".DS_Store", "._*", "__MACOSX/", ".Spotlight-V100/", ".Trashes/", ".fseventsd/", ".TemporaryItems/",
        // Windows
        "Thumbs.db", "ehthumbs.db", "desktop.ini", "$RECYCLE.BIN/", "System Volume Information/",
        // Office / LibreOffice
        "~$*", ".~lock.*#",
        // Vim
        "*.swp", "*.swo", "*.swn", ".*.swp", ".*.swo", ".*.swn",
        // Emacs
        "#*#", ".#*",
        // Unix / NFS / Samba
        ".nfs*", ".smbdelete*",
    ];

    /// <summary>入力テキスト（1 行に 1 つ）をパターンの一覧に分ける。</summary>
    public static List<string> Split(string? text) =>
        [.. (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>前後の空白を除き、空行と重複（大文字小文字を区別しない）を取り除いて、検証する。</summary>
    public static (List<string> Patterns, List<LocalizedMessage> Errors) Normalize(IEnumerable<string> input)
    {
        var patterns = new List<string>();
        var errors = new List<LocalizedMessage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in input)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0 || !seen.Add(pattern))
            {
                continue;
            }

            if (pattern.Length > MaxPatternLength)
            {
                errors.Add(new("Error.ExcludePattern.TooLong", MaxPatternLength, Shorten(pattern)));
            }
            else if (pattern.Any(char.IsControl))
            {
                errors.Add(new("Error.ExcludePattern.ControlChar", Shorten(pattern)));
            }
            else if (!HasValidShape(pattern))
            {
                errors.Add(new("Error.ExcludePattern.InvalidShape", Shorten(pattern)));
            }

            patterns.Add(pattern);
        }

        if (patterns.Count > MaxPatterns)
        {
            errors.Add(new("Error.ExcludePattern.TooMany", MaxPatterns));
        }

        if (string.Join('\n', patterns).Length > MaxTotalLength)
        {
            errors.Add(new("Error.ExcludePattern.TotalTooLong", MaxTotalLength));
        }

        return (patterns, errors);
    }

    /// <summary>末尾の / （または \）を除いた本体が空でなく、名前に使えない区切り文字を含まない。</summary>
    private static bool HasValidShape(string pattern)
    {
        var body = pattern.TrimEnd('/', '\\');
        return body.Length > 0
            && pattern.Length - body.Length <= 1
            && body.IndexOfAny(['/', '\\']) < 0;
    }

    private static string Shorten(string text) => text.Length <= 30 ? text : text[..30] + "…";
}
