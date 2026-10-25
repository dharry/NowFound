namespace NowFound.Application;

// 一覧の検索条件と並べ替えキー。キーは画面の列と対応し、サービス側で許可したものだけを使う（不明なキーは既定の並びにする）。

/// <param name="Search">メールアドレスの部分一致。</param>
/// <param name="Role">"admin" か "user" で絞り込む。それ以外は絞り込まない。</param>
public sealed record UserListQuery(string? Search = null, string? Role = null, string? Sort = null, bool Descending = false);

/// <param name="Search">名前・説明の部分一致。</param>
public sealed record GroupListQuery(string? Search = null, string? Sort = null, bool Descending = false);

/// <param name="Search">名前・パスの部分一致。</param>
public sealed record FolderListQuery(string? Search = null, string? Sort = null, bool Descending = false);

/// <param name="Search">名前・説明・作成者の部分一致。</param>
/// <param name="Visibility">"mine"（非公開）か "public"（公開）で絞り込む。それ以外は絞り込まない。</param>
public sealed record SavedSearchListQuery(string? Search = null, string? Visibility = null, string? Sort = null, bool Descending = false);

public static class UserSortKeys
{
    public const string Email = "email";
    public const string Role = "role";
    public const string MustChange = "mustchange";
}

public static class GroupSortKeys
{
    public const string Name = "name";
    public const string Description = "description";
    public const string Members = "members";
    public const string Folders = "folders";
}

public static class FolderSortKeys
{
    public const string Name = "name";
    public const string Path = "path";
    public const string Excludes = "excludes";
    public const string Groups = "groups";
    public const string Share = "share";
}

public static class BookmarkSortKeys
{
    public const string Name = "name";
    public const string Description = "description";
    public const string Owner = "owner";
    public const string Updated = "updated";
}

public static class LogSortKeys
{
    public const string Time = "time";
    public const string Operation = "operation";
    public const string User = "user";
    public const string Ip = "ip";
}
