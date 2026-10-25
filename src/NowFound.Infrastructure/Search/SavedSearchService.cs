using Microsoft.EntityFrameworkCore;
using NowFound.Application;
using NowFound.Application.Search;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Search;

public class SavedSearchService(AppDbContext db) : ISavedSearchService
{
    public async Task<IReadOnlyList<SavedSearchSummary>> ListAsync(string userId, bool isAdmin, CancellationToken ct = default)
    {
        var rows = await ToSummaries(db.SavedSearches.AsNoTracking().Where(s => s.IsPublic || s.OwnerUserId == userId))
            .ToListAsync(ct);

        return
        [
            .. rows
                .OrderByDescending(r => r.Search.IsPinned)
                .ThenBy(r => r.Search.Name, StringComparer.Ordinal)
                .Select(r => Map(r, userId, isAdmin)),
        ];
    }

    public async Task<PagedResult<SavedSearchSummary>> ListPageAsync(
        string userId, bool isAdmin, int page, int pageSize, SavedSearchListQuery? query = null, CancellationToken ct = default)
    {
        // 名前の並びを序数比較にそろえるため、検索・並べ替えはメモリで行う（ブックマークは件数が小さい）。画面に渡すのは 1 ページ分だけ。
        query ??= new SavedSearchListQuery();
        IEnumerable<SavedSearchSummary> items = await ListAsync(userId, isAdmin, ct);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var text = query.Search.Trim();
            items = items.Where(s => Contains(s.Name, text) || Contains(s.Description, text) || Contains(s.OwnerEmail, text));
        }

        items = query.Visibility switch
        {
            "mine" => items.Where(s => !s.IsPublic),
            "public" => items.Where(s => s.IsPublic),
            _ => items,
        };

        var filtered = Sort(items, query).ToList();
        page = PagedResult<SavedSearchSummary>.ClampPage(page, filtered.Count, pageSize);
        return new PagedResult<SavedSearchSummary>([.. filtered.Skip((page - 1) * pageSize).Take(pageSize)], page, pageSize, filtered.Count);
    }

    private static bool Contains(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>並べ替えの指定がなければ、固定（ピン留め）が先頭、次に名前順。指定があればその列だけで並べる（同じ値は名前順）。</summary>
    private static IEnumerable<SavedSearchSummary> Sort(IEnumerable<SavedSearchSummary> items, SavedSearchListQuery query)
    {
        var byName = StringComparer.Ordinal;
        IOrderedEnumerable<SavedSearchSummary> ordered = (query.Sort, query.Descending) switch
        {
            (BookmarkSortKeys.Name, false) => items.OrderBy(s => s.Name, byName),
            (BookmarkSortKeys.Name, true) => items.OrderByDescending(s => s.Name, byName),
            (BookmarkSortKeys.Description, false) => items.OrderBy(s => s.Description ?? string.Empty, byName),
            (BookmarkSortKeys.Description, true) => items.OrderByDescending(s => s.Description ?? string.Empty, byName),
            (BookmarkSortKeys.Owner, false) => items.OrderBy(s => s.OwnerEmail, byName),
            (BookmarkSortKeys.Owner, true) => items.OrderByDescending(s => s.OwnerEmail, byName),
            (BookmarkSortKeys.Updated, false) => items.OrderBy(s => s.UpdatedAt),
            (BookmarkSortKeys.Updated, true) => items.OrderByDescending(s => s.UpdatedAt),
            _ => items.OrderByDescending(s => s.IsPinned),
        };
        return ordered.ThenBy(s => s.Name, byName).ThenBy(s => s.Id);
    }

    public async Task<SavedSearchDetail?> GetAsync(int id, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var row = await ToSummaries(db.SavedSearches.AsNoTracking()
                .Where(s => s.Id == id && (s.IsPublic || s.OwnerUserId == userId)))
            .SingleOrDefaultAsync(ct);

        return row is null ? null : new SavedSearchDetail(Map(row, userId, isAdmin), SavedQueryJson.TryDeserialize(row.Search.QueryJson));
    }

    public async Task<SavedSearchCreateResult> CreateAsync(
        string userId, bool isAdmin, SavedSearchMeta meta, SavedQuery query, CancellationToken ct = default)
    {
        var (name, description, error) = ValidateMeta(meta);
        if (error is not null)
        {
            return Fail(error);
        }

        if (meta.IsPublic && !isAdmin)
        {
            return Fail(new("Error.SavedSearch.PublicCreateAdminOnly"));
        }

        var normalized = SavedQueryRules.Normalize(query);
        if (SavedQueryRules.Validate(normalized) is { } queryError)
        {
            return Fail(queryError);
        }

        if (await ScopeError(userId, meta.IsPublic, name, excludeId: 0, countsAgainstLimit: true, ct) is { } scopeError)
        {
            return Fail(scopeError);
        }

        var now = DateTime.UtcNow;
        var entity = new SavedSearch
        {
            OwnerUserId = userId,
            Name = name,
            Description = description,
            IsPublic = meta.IsPublic,
            IsPinned = meta.IsPinned,
            QueryJson = SavedQueryJson.Serialize(normalized),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SavedSearches.Add(entity);
        await db.SaveChangesAsync(ct);
        return new SavedSearchCreateResult(OperationResult.Success, entity.Id);
    }

    public async Task<OperationResult> UpdateAsync(
        int id, string userId, bool isAdmin, SavedSearchMeta meta, SavedQuery? newQuery, CancellationToken ct = default)
    {
        var (entity, accessError) = await LoadEditableAsync(id, userId, isAdmin, ct);
        if (entity is null)
        {
            return OperationResult.Failure([accessError!]);
        }

        var (name, description, error) = ValidateMeta(meta);
        if (error is not null)
        {
            return OperationResult.Failure([error]);
        }

        if (meta.IsPublic && !isAdmin)
        {
            return OperationResult.Failure("Error.SavedSearch.PublicAdminOnly");
        }

        if (entity.IsPublic && !meta.IsPublic && entity.OwnerUserId != userId)
        {
            return OperationResult.Failure("Error.SavedSearch.UnpublishCreatorOnly");
        }

        var scopeChanged = entity.IsPublic != meta.IsPublic;
        var ownerForScope = entity.OwnerUserId;
        if (await ScopeError(ownerForScope, meta.IsPublic, name, entity.Id, countsAgainstLimit: scopeChanged, ct) is { } scopeError)
        {
            return OperationResult.Failure([scopeError]);
        }

        if (newQuery is not null)
        {
            var normalized = SavedQueryRules.Normalize(newQuery);
            if (SavedQueryRules.Validate(normalized) is { } queryError)
            {
                return OperationResult.Failure([queryError]);
            }

            entity.QueryJson = SavedQueryJson.Serialize(normalized);
        }

        entity.Name = name;
        entity.Description = description;
        entity.IsPublic = meta.IsPublic;
        entity.IsPinned = meta.IsPinned;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> OverwriteQueryAsync(
        int id, string userId, bool isAdmin, SavedQuery query, CancellationToken ct = default)
    {
        var (entity, accessError) = await LoadEditableAsync(id, userId, isAdmin, ct);
        if (entity is null)
        {
            return OperationResult.Failure([accessError!]);
        }

        var normalized = SavedQueryRules.Normalize(query);
        if (SavedQueryRules.Validate(normalized) is { } queryError)
        {
            return OperationResult.Failure([queryError]);
        }

        entity.QueryJson = SavedQueryJson.Serialize(normalized);
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> DeleteAsync(int id, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var (entity, accessError) = await LoadEditableAsync(id, userId, isAdmin, ct);
        if (entity is null)
        {
            return OperationResult.Failure([accessError!]);
        }

        db.SavedSearches.Remove(entity);
        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }

    private IQueryable<Row> ToSummaries(IQueryable<SavedSearch> searches) =>
        from s in searches
        join u in db.Users on s.OwnerUserId equals u.Id into owners
        from owner in owners.DefaultIfEmpty()
        select new Row(s, owner == null ? string.Empty : owner.Email ?? string.Empty);

    private static SavedSearchSummary Map(Row row, string userId, bool isAdmin)
    {
        var s = row.Search;
        var canEdit = s.IsPublic ? isAdmin : s.OwnerUserId == userId;
        return new SavedSearchSummary(s.Id, s.Name, s.Description, s.IsPublic, s.IsPinned, canEdit, row.OwnerEmail, s.UpdatedAt);
    }

    /// <summary>見えて、かつ編集できるものだけを返す。見えないものは「見つからない」として扱い、存在を悟らせない。</summary>
    private async Task<(SavedSearch? Entity, LocalizedMessage? Error)> LoadEditableAsync(
        int id, string userId, bool isAdmin, CancellationToken ct)
    {
        var entity = await db.SavedSearches.SingleOrDefaultAsync(
            s => s.Id == id && (s.IsPublic || s.OwnerUserId == userId), ct);
        if (entity is null)
        {
            return (null, new("Error.SavedSearch.NotFound"));
        }

        var canEdit = entity.IsPublic ? isAdmin : entity.OwnerUserId == userId;
        return canEdit ? (entity, null) : (null, new("Error.SavedSearch.NoPermission"));
    }

    private static (string Name, string? Description, LocalizedMessage? Error) ValidateMeta(SavedSearchMeta meta)
    {
        var name = meta.Name?.Trim() ?? string.Empty;
        var description = string.IsNullOrWhiteSpace(meta.Description) ? null : meta.Description.Trim();

        if (name.Length == 0)
        {
            return (name, description, new("Error.SavedSearch.NameRequired"));
        }

        if (name.Length > SavedSearchLimits.MaxNameLength)
        {
            return (name, description, new("Error.SavedSearch.NameTooLong", SavedSearchLimits.MaxNameLength));
        }

        if (description is not null && description.Length > SavedSearchLimits.MaxDescriptionLength)
        {
            return (name, description, new("Error.SavedSearch.DescriptionTooLong", SavedSearchLimits.MaxDescriptionLength));
        }

        return (name, description, null);
    }

    /// <summary>同じ範囲（本人の非公開どうし／公開どうし）での名前の重複と、件数の上限を確認する。</summary>
    private async Task<LocalizedMessage?> ScopeError(
        string ownerUserId, bool isPublic, string name, int excludeId, bool countsAgainstLimit, CancellationToken ct)
    {
        var sameScope = isPublic
            ? db.SavedSearches.Where(s => s.IsPublic)
            : db.SavedSearches.Where(s => !s.IsPublic && s.OwnerUserId == ownerUserId);

        if (await sameScope.AnyAsync(s => s.Name == name && s.Id != excludeId, ct))
        {
            return new(isPublic ? "Error.SavedSearch.DuplicatePublic" : "Error.SavedSearch.Duplicate");
        }

        if (countsAgainstLimit)
        {
            var limit = isPublic ? SavedSearchLimits.MaxPublic : SavedSearchLimits.MaxPrivatePerUser;
            if (await sameScope.CountAsync(ct) >= limit)
            {
                return isPublic
                    ? new("Error.SavedSearch.PublicLimit", SavedSearchLimits.MaxPublic)
                    : new("Error.SavedSearch.PrivateLimit", SavedSearchLimits.MaxPrivatePerUser);
            }
        }

        return null;
    }

    private static SavedSearchCreateResult Fail(LocalizedMessage error) =>
        new(OperationResult.Failure([error]), null);

    private sealed record Row(SavedSearch Search, string OwnerEmail);
}
