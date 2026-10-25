using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NowFound.Application;
using NowFound.Application.Indexing;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Search;

public class SearchService(AppDbContext mainDb, FilesDbContext filesDb, IAdminSettingsService settings) : ISearchService
{
    /// <summary>FTS5 trigram は 3 文字以上でのみ使える。</summary>
    private const int MinFtsLength = 3;



    public async Task<IReadOnlyList<AccessibleFolder>> GetAccessibleFoldersAsync(
        string userId, bool isAdmin, CancellationToken ct = default) =>
        await AccessibleFolders(userId, isAdmin).OrderBy(f => f.Name)
            .Select(f => new AccessibleFolder(f.Id, f.Name, f.Path))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SubFolder>> GetSubFoldersAsync(
        string userId, bool isAdmin, int folderId, string? parentPath, CancellationToken ct = default)
    {
        var root = await AccessibleFolders(userId, isAdmin).Where(f => f.Id == folderId)
            .Select(f => f.Path).SingleOrDefaultAsync(ct);
        if (root is null)
        {
            return [];
        }

        var parent = string.IsNullOrEmpty(parentPath) ? root : parentPath;
        if (!IsUnderRoot(root, parent))
        {
            return [];
        }

        var names = await filesDb.Files.AsNoTracking()
            .Where(f => f.SearchFolderId == folderId && f.IsDirectory && f.Directory!.Path == parent)
            .OrderBy(f => f.Name)
            .Select(f => f.Name)
            .ToListAsync(ct);
        return [.. names.Select(name => new SubFolder(name, FilePaths.Combine(parent, name)))];
    }

    private sealed record AllowedFolder(int Id, string Path, bool IsShared, string? ShareName);

    /// <summary>
    /// 権限・フォルダ絞り込み・検索条件を適用した問い合わせ（並べ替え・ページ送りの前）。
    /// 絞り込み先が見えない・配下でない場合は Query が null（結果は空）。検索とエクスポートで共有する。
    /// </summary>
    private async Task<(IQueryable<FileEntry>? Query, Dictionary<int, AllowedFolder> Folders)> BuildQueryAsync(
        SearchRequest request, CancellationToken ct)
    {
        var allowed = await AccessibleFolders(request.UserId, request.IsAdmin)
            .Select(f => new AllowedFolder(f.Id, f.Path, f.IsShared, f.ShareName)).ToListAsync(ct);
        var folders = allowed.ToDictionary(a => a.Id);
        var allowedIds = allowed.Select(a => a.Id).ToList();

        IQueryable<FileEntry> query = filesDb.Files.AsNoTracking().Where(f => allowedIds.Contains(f.SearchFolderId));

        if (request.FolderId is { } folderId)
        {
            var root = allowed.FirstOrDefault(a => a.Id == folderId)?.Path;
            if (root is null)
            {
                return (null, folders);
            }

            query = query.Where(f => f.SearchFolderId == folderId);
            if (!string.IsNullOrEmpty(request.FolderPath))
            {
                if (!IsUnderRoot(root, request.FolderPath))
                {
                    return (null, folders);
                }

                // 指定フォルダ自身（親フォルダの中のエントリ）と、その配下すべて。
                var exact = request.FolderPath;
                var trimmed = exact.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var prefix = trimmed + Path.DirectorySeparatorChar;
                var parent = Path.GetDirectoryName(trimmed);
                var leaf = Path.GetFileName(trimmed);
                query = query.Where(f => f.Directory!.Path == exact || f.Directory.Path.StartsWith(prefix)
                    || (f.Directory.Path == parent && f.Name == leaf));
            }
        }

        var predicate = BuildPredicate(request.Conditions, request.Operator, request.TimeZone);
        if (predicate is not null)
        {
            query = query.Where(predicate);
        }

        return (query, folders);
    }

    private sealed record ResultRow(
        int SearchFolderId, string FolderPath, string Name, bool IsDirectory, long? Size, string Extension,
        string? OwnerName, DateTime ModifiedAt, DateTime CreatedAt);

    private static IQueryable<ResultRow> ToRows(IQueryable<FileEntry> query) =>
        query.Select(f => new ResultRow(
            f.SearchFolderId, f.Directory!.Path, f.Name, f.IsDirectory, f.Size, f.Extension,
            f.Owner == null ? null : f.Owner.Name, f.ModifiedAt, f.CreatedAt));

    private static SearchResultItem ToItem(ResultRow i, Dictionary<int, AllowedFolder> folders)
    {
        var fullPath = FilePaths.Combine(i.FolderPath, i.Name);
        var share = folders[i.SearchFolderId];
        return new SearchResultItem(
            i.SearchFolderId, fullPath, i.Name, i.FolderPath, i.IsDirectory,
            i.Size, i.Extension, i.OwnerName, i.ModifiedAt, i.CreatedAt,
            SharePaths.ToDisplayPath(fullPath, share.Path, share.IsShared, share.ShareName, Environment.MachineName),
            SharePaths.ToDisplayPath(i.FolderPath, share.Path, share.IsShared, share.ShareName, Environment.MachineName));
    }

    public async Task<SearchPage> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var pageSize = await ResolvePageSizeAsync(request.UserId, ct);
        var page = Math.Max(1, request.Page);

        var (query, folders) = await BuildQueryAsync(request, ct);
        if (query is null)
        {
            return new SearchPage([], page, pageSize, 0);
        }

        var total = await query.CountAsync(ct);
        var rows = await ToRows(Order(query, request.SortKey, request.Descending)
                .Skip((page - 1) * pageSize)
                .Take(pageSize))
            .ToListAsync(ct);

        return new SearchPage([.. rows.Select(r => ToItem(r, folders))], page, pageSize, total);
    }

    public async IAsyncEnumerable<SearchResultItem> ExportAsync(
        SearchRequest request, int maxRows, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (query, folders) = await BuildQueryAsync(request, ct);
        if (query is null || maxRows <= 0)
        {
            yield break;
        }

        // 件数集計も全件ロードもせず、リーダーから 1 行ずつ流す。
        var rows = ToRows(Order(query, request.SortKey, request.Descending).Take(maxRows)).AsAsyncEnumerable();
        await foreach (var row in rows.WithCancellation(ct))
        {
            yield return ToItem(row, folders);
        }
    }

    /// <summary>指定キーで並べ替える。同順位はフォルダパス・名前順（インデックス適用可能なカラム別ソート）で安定させる。</summary>
    private static IQueryable<FileEntry> Order(IQueryable<FileEntry> query, SearchSortKey key, bool descending)
    {
        IOrderedQueryable<FileEntry> ordered = (key, descending) switch
        {
            (SearchSortKey.Name, false) => query.OrderBy(f => f.Name),
            (SearchSortKey.Name, true) => query.OrderByDescending(f => f.Name),
            (SearchSortKey.Folder, false) => query.OrderBy(f => f.Directory!.Path),
            (SearchSortKey.Folder, true) => query.OrderByDescending(f => f.Directory!.Path),
            (SearchSortKey.Size, false) => query.OrderBy(f => f.Size),
            (SearchSortKey.Size, true) => query.OrderByDescending(f => f.Size),
            (SearchSortKey.Extension, false) => query.OrderBy(f => f.Extension),
            (SearchSortKey.Extension, true) => query.OrderByDescending(f => f.Extension),
            (SearchSortKey.Owner, false) => query.OrderBy(f => f.Owner == null ? null : f.Owner.Name),
            (SearchSortKey.Owner, true) => query.OrderByDescending(f => f.Owner == null ? null : f.Owner.Name),
            (SearchSortKey.Modified, false) => query.OrderBy(f => f.ModifiedAt),
            (SearchSortKey.Modified, true) => query.OrderByDescending(f => f.ModifiedAt),
            (SearchSortKey.Created, false) => query.OrderBy(f => f.CreatedAt),
            (SearchSortKey.Created, true) => query.OrderByDescending(f => f.CreatedAt),
            (_, true) => query.OrderByDescending(f => f.Directory!.Path).ThenByDescending(f => f.Name),
            _ => query.OrderBy(f => f.Directory!.Path).ThenBy(f => f.Name),
        };

        return ordered.ThenBy(f => f.Directory!.Path).ThenBy(f => f.Name);
    }

    public async Task<int> ResolvePageSizeAsync(string userId, CancellationToken ct = default)
    {
        var own = await mainDb.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => u.PageSize).SingleOrDefaultAsync(ct);
        return own is { } size && PageSizeLimits.IsValid(size) ? size : await settings.GetDefaultPageSizeAsync(ct);
    }

    private IQueryable<SearchFolder> AccessibleFolders(string userId, bool isAdmin) =>
        isAdmin
            ? mainDb.SearchFolders.AsNoTracking()
            : mainDb.SearchFolders.AsNoTracking().Where(f =>
                f.GroupSearchFolders.Any(gs => gs.Group!.UserGroups.Any(ug => ug.UserId == userId)));

    private static bool IsUnderRoot(string root, string path)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.Equals(trimmedRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private Expression<Func<FileEntry, bool>>? BuildPredicate(
        IReadOnlyList<SearchCondition> conditions, SearchOperator op, TimeZoneInfo? timeZone)
    {
        Expression<Func<FileEntry, bool>>? combined = null;
        foreach (var condition in conditions.Where(c => !c.IsEmpty))
        {
            var next = BuildCondition(condition, timeZone);
            combined = combined is null ? next : Combine(combined, next, op);
        }

        return combined;
    }

    private Expression<Func<FileEntry, bool>> BuildCondition(SearchCondition c, TimeZoneInfo? timeZone)
    {
        switch (c.Field)
        {
            case SearchField.Extension:
            {
                var extensions = c.Text!
                    .Split([',', ' ', ';', '　'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.TrimStart('.').ToLowerInvariant())
                    .ToList();
                return f => extensions.Contains(f.Extension);
            }

            case SearchField.ModifiedDate:
            {
                var (from, to) = DateRange(c, timeZone);
                return f => (from == null || f.ModifiedAt >= from) && (to == null || f.ModifiedAt < to);
            }

            case SearchField.CreatedDate:
            {
                var (from, to) = DateRange(c, timeZone);
                return f => (from == null || f.CreatedAt >= from) && (to == null || f.CreatedAt < to);
            }

            case SearchField.Size:
                return SizeCondition(c.SizeComparison, c.SizeBytes!.Value);

            case SearchField.Owner:
                return LikeCondition(f => f.Owner!.Name, c.Text!.Trim());

            case SearchField.FileName:
                return NameCondition(c.Text!.Trim());

            case SearchField.FolderName:
                return FolderCondition(c.Text!.Trim());

            case SearchField.All:
            default:
                return AllCondition(c.Text!.Trim());
        }
    }

    /// <summary>
    /// 「すべて」検索（キーワード検索）。
    /// 3文字以上は FTS5（ファイル名・フォルダ名）と拡張子一致を SQL の UNION で結合し、
    /// Files テーブルのフルスキャンを回避して Id のインデックス走査で取得する。
    /// 3文字未満は FTS5 が使えないため LIKE によるフォールバックを行う。
    /// </summary>
    private Expression<Func<FileEntry, bool>> AllCondition(string text)
    {
        var ext = text.TrimStart('.').ToLowerInvariant();

        if (text.Length >= MinFtsLength)
        {
            var phrase = ToPhrase(text);
            IQueryable<long> matchingIds;

            if (!string.IsNullOrEmpty(ext))
            {
                matchingIds = filesDb.Database.SqlQuery<long>($"""
                    SELECT rowid AS Value FROM FilesFts WHERE FilesFts MATCH {phrase}
                    UNION
                    SELECT Id AS Value FROM Files WHERE DirectoryId IN (SELECT rowid FROM DirectoriesFts WHERE DirectoriesFts MATCH {phrase})
                    UNION
                    SELECT Id AS Value FROM Files WHERE Extension = {ext}
                    """);
            }
            else
            {
                matchingIds = filesDb.Database.SqlQuery<long>($"""
                    SELECT rowid AS Value FROM FilesFts WHERE FilesFts MATCH {phrase}
                    UNION
                    SELECT Id AS Value FROM Files WHERE DirectoryId IN (SELECT rowid FROM DirectoriesFts WHERE DirectoriesFts MATCH {phrase})
                    """);
            }

            return f => matchingIds.Contains(f.Id);
        }

        var inName = NameCondition(text);
        var inFolder = FolderCondition(text);
        if (!string.IsNullOrEmpty(ext))
        {
            Expression<Func<FileEntry, bool>> extMatch = f => f.Extension == ext;
            return Combine(Combine(inName, inFolder, SearchOperator.Or), extMatch, SearchOperator.Or);
        }

        return Combine(inName, inFolder, SearchOperator.Or);
    }

    /// <summary>
    /// サイズの比較。サイズを持たないフォルダ（Size が null）は、どの比較にも一致しない。
    /// 「等しくない」でもフォルダが紛れ込まないよう、null でないことを明示する。
    /// </summary>
    private static Expression<Func<FileEntry, bool>> SizeCondition(SizeComparison comparison, long bytes) => comparison switch
    {
        SizeComparison.GreaterThan => f => f.Size != null && f.Size > bytes,
        SizeComparison.LessOrEqual => f => f.Size != null && f.Size <= bytes,
        SizeComparison.LessThan => f => f.Size != null && f.Size < bytes,
        SizeComparison.Equal => f => f.Size != null && f.Size == bytes,
        SizeComparison.NotEqual => f => f.Size != null && f.Size != bytes,
        _ => f => f.Size != null && f.Size >= bytes,
    };

    /// <summary>ファイル名の部分一致。3 文字以上は FTS5（trigram）、それ未満は LIKE。</summary>
    private Expression<Func<FileEntry, bool>> NameCondition(string text)
    {
        if (text.Length >= MinFtsLength)
        {
            var phrase = ToPhrase(text);
            var ids = filesDb.Database.SqlQuery<long>($"SELECT rowid AS Value FROM FilesFts WHERE FilesFts MATCH {phrase}");
            return f => ids.Contains(f.Id);
        }

        return LikeCondition(f => f.Name, text);
    }

    /// <summary>
    /// フォルダパスの部分一致。パスは Directories 表に 1 回だけ保存しているため、フォルダ側の FTS で
    /// 一致したフォルダを求め、そこに含まれるエントリを対象にする。
    /// </summary>
    private Expression<Func<FileEntry, bool>> FolderCondition(string text)
    {
        if (text.Length >= MinFtsLength)
        {
            var phrase = ToPhrase(text);
            var directoryIds = filesDb.Database.SqlQuery<int>(
                $"SELECT rowid AS Value FROM DirectoriesFts WHERE DirectoriesFts MATCH {phrase}");
            return f => directoryIds.Contains(f.DirectoryId);
        }

        return LikeCondition(f => f.Directory!.Path, text);
    }

    /// <summary>FTS5 のフレーズ（ダブルクォートで囲み、中の " は二重にする）。演算子や記号を文字として扱う。</summary>
    private static string ToPhrase(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

    private static Expression<Func<FileEntry, bool>> LikeCondition(Expression<Func<FileEntry, string>> selector, string text)
    {
        var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var body = Expression.Call(
            typeof(DbFunctionsExtensions), nameof(DbFunctionsExtensions.Like), Type.EmptyTypes,
            Expression.Constant(EF.Functions), selector.Body, Expression.Constant(pattern), Expression.Constant("\\"));
        return Expression.Lambda<Func<FileEntry, bool>>(body, selector.Parameters);
    }

    private static (DateTime? From, DateTime? To) DateRange(SearchCondition c, TimeZoneInfo? timeZone) =>
        DateTimeRange.ToUtc(c.From, c.To, timeZone);

    private static Expression<Func<FileEntry, bool>> Combine(
        Expression<Func<FileEntry, bool>> left, Expression<Func<FileEntry, bool>> right, SearchOperator op)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body);
        var body = op == SearchOperator.And
            ? Expression.AndAlso(left.Body, rightBody)
            : Expression.OrElse(left.Body, rightBody);
        return Expression.Lambda<Func<FileEntry, bool>>(body, parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
