using NowFound.Application.Search;

namespace NowFound.Web.Services;

/// <summary>絞り込み中のフォルダを「表示名\サブフォルダ\…」の形で表す（絶対パスは見せない）。</summary>
public static class FolderScopeLabel
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <param name="root">検索対象フォルダ（絞り込みの起点）。見つからなければ null。</param>
    /// <param name="folderPath">サブフォルダの絶対パス。空なら検索対象フォルダ自身。</param>
    public static string? Build(AccessibleFolder? root, string? folderPath)
    {
        if (root is null)
        {
            return string.IsNullOrEmpty(folderPath) ? null : folderPath;
        }

        if (string.IsNullOrEmpty(folderPath))
        {
            return root.Name;
        }

        var rootPath = root.Path.Replace('/', '\\').TrimEnd(Separators);
        var path = folderPath.Replace('/', '\\').TrimEnd(Separators);
        if (path.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return root.Name;
        }

        if (path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) && path.Length > rootPath.Length && Separators.Contains(path[rootPath.Length]))
        {
            var segments = path[(rootPath.Length + 1)..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            return string.Join('\\', [root.Name, .. segments]);
        }

        // 起点の配下でないパスは、そのまま見せるしかない（検索結果は空になる）。
        return folderPath;
    }
}
