namespace NowFound.Application.Search;

/// <summary>共有フォルダ配下のローカルパスを UNC パス（\\コンピューター名\共有名\...）へ差し替える。</summary>
public static class SharePaths
{
    private static readonly char[] InvalidShareNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>共有名として使える文字だけか。</summary>
    public static bool IsValidShareName(string name) =>
        name.Length > 0 && name.IndexOfAny(InvalidShareNameChars) < 0;

    /// <summary>共有名が未設定のときの既定値。パス末尾のフォルダ名。ドライブ直下などで取れなければ表示名。</summary>
    public static string DefaultShareName(string rootPath, string displayName)
    {
        var leaf = Path.GetFileName(rootPath.TrimEnd('\\', '/'));
        return leaf.Length > 0 ? leaf : displayName;
    }

    /// <summary>
    /// path が rootPath 配下なら、rootPath 部分を \\computerName\shareName に置き換えた値を返す。
    /// 共有フォルダでない・配下でない場合は path をそのまま返す。
    /// </summary>
    public static string ToDisplayPath(string path, string rootPath, bool isShared, string? shareName, string computerName)
    {
        if (!isShared || string.IsNullOrWhiteSpace(shareName))
        {
            return path;
        }

        var root = rootPath.TrimEnd('\\', '/');
        var unc = $@"\\{computerName}\{shareName}";
        if (path.TrimEnd('\\', '/').Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return unc;
        }

        var isUnder = path.Length > root.Length
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && path[root.Length] is '\\' or '/';
        return isUnder ? unc + @"\" + path[(root.Length + 1)..].Replace('/', '\\') : path;
    }
}
