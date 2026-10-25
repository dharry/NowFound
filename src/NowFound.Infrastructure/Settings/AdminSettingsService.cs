using System.Globalization;
using NowFound.Application;
using Microsoft.EntityFrameworkCore;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Settings;

public class AdminSettingsService(AppDbContext db) : IAdminSettingsService
{
    public async Task<IReadOnlyList<FolderSummary>> ListFoldersAsync(CancellationToken ct = default) =>
        await db.SearchFolders.AsNoTracking().OrderBy(f => f.Name)
            .Select(f => new FolderSummary(f.Id, f.Name, f.Path, f.ExcludePatterns.Count, f.GroupSearchFolders.Count, f.IsShared, f.ShareName))
            .ToListAsync(ct);

    public async Task<PagedResult<FolderSummary>> ListFoldersPageAsync(
        int page, int pageSize, FolderListQuery? query = null, CancellationToken ct = default)
    {
        query ??= new FolderListQuery();
        var folders = db.SearchFolders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = SqlLike.Contains(query.Search);
            folders = folders.Where(f => EF.Functions.Like(f.Name, pattern, SqlLike.Escape)
                || EF.Functions.Like(f.Path, pattern, SqlLike.Escape)
                || (f.ShareName != null && EF.Functions.Like(f.ShareName, pattern, SqlLike.Escape)));
        }

        var total = await folders.CountAsync(ct);
        page = PagedResult<FolderSummary>.ClampPage(page, total, pageSize);
        var ordered = (query.Sort, query.Descending) switch
        {
            (FolderSortKeys.Path, false) => folders.OrderBy(f => f.Path),
            (FolderSortKeys.Path, true) => folders.OrderByDescending(f => f.Path),
            (FolderSortKeys.Excludes, false) => folders.OrderBy(f => f.ExcludePatterns.Count),
            (FolderSortKeys.Excludes, true) => folders.OrderByDescending(f => f.ExcludePatterns.Count),
            (FolderSortKeys.Groups, false) => folders.OrderBy(f => f.GroupSearchFolders.Count),
            (FolderSortKeys.Groups, true) => folders.OrderByDescending(f => f.GroupSearchFolders.Count),
            // 共有していないフォルダは共有名を持たないので、共有フォルダが先（昇順）／後（降順）にまとまる。
            (FolderSortKeys.Share, false) => folders.OrderByDescending(f => f.IsShared).ThenBy(f => f.ShareName),
            (FolderSortKeys.Share, true) => folders.OrderBy(f => f.IsShared).ThenByDescending(f => f.ShareName),
            (FolderSortKeys.Name, true) => folders.OrderByDescending(f => f.Name),
            _ => folders.OrderBy(f => f.Name),
        };
        var items = await ordered.ThenBy(f => f.Name).ThenBy(f => f.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(f => new FolderSummary(f.Id, f.Name, f.Path, f.ExcludePatterns.Count, f.GroupSearchFolders.Count, f.IsShared, f.ShareName))
            .ToListAsync(ct);
        return new PagedResult<FolderSummary>(items, page, pageSize, total);
    }

    public async Task<FolderEdit?> GetFolderAsync(int id, CancellationToken ct = default) =>
        await db.SearchFolders.AsNoTracking().Where(f => f.Id == id)
            .Select(f => new FolderEdit
            {
                Id = f.Id,
                Name = f.Name,
                Path = f.Path,
                ExcludeHidden = f.ExcludeHidden,
                IsShared = f.IsShared,
                ShareName = f.ShareName,
                ExcludePatterns = f.ExcludePatterns.OrderBy(p => p.Id).Select(p => p.Pattern).ToList(),
            })
            .SingleOrDefaultAsync(ct);

    public async Task<OperationResult> SaveFolderAsync(FolderEdit folder, CancellationToken ct = default)
    {
        var name = folder.Name?.Trim() ?? string.Empty;
        var path = folder.Path?.Trim() ?? string.Empty;
        if (name.Length == 0 || path.Length == 0)
        {
            return OperationResult.Failure("Error.Folder.NameAndPathRequired");
        }

        // UNC パス（\\server\share、//server/share）は指定できない。ネットワーク上の共有は、サーバー側でマウントしたパスを使う。
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return OperationResult.Failure("Error.Folder.PathUnc");
        }

        if (!Path.IsPathRooted(path))
        {
            return OperationResult.Failure("Error.Folder.PathNotAbsolute");
        }

        path = path.Length > 3 ? path.TrimEnd('\\', '/') : path;
        if (await db.SearchFolders.AnyAsync(f => f.Path == path && f.Id != folder.Id, ct))
        {
            return OperationResult.Failure("Error.Folder.PathDuplicate");
        }

        var shareName = folder.ShareName?.Trim();
        if (folder.IsShared)
        {
            if (string.IsNullOrEmpty(shareName))
            {
                shareName = SharePaths.DefaultShareName(path, name);
            }

            if (!SharePaths.IsValidShareName(shareName))
            {
                return OperationResult.Failure("Error.Folder.ShareNameInvalid");
            }
        }

        SearchFolder entity;
        if (folder.Id == 0)
        {
            entity = new SearchFolder();
            db.SearchFolders.Add(entity);
        }
        else
        {
            var found = await db.SearchFolders.Include(f => f.ExcludePatterns).SingleOrDefaultAsync(f => f.Id == folder.Id, ct);
            if (found is null)
            {
                return OperationResult.Failure("Error.Folder.NotFound");
            }

            entity = found;
            db.ExcludePatterns.RemoveRange(entity.ExcludePatterns);
            entity.ExcludePatterns.Clear();
        }

        entity.Name = name;
        entity.Path = path;
        entity.ExcludeHidden = folder.ExcludeHidden;
        entity.IsShared = folder.IsShared;
        entity.ShareName = folder.IsShared ? shareName : null;
        foreach (var pattern in folder.ExcludePatterns.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct())
        {
            entity.ExcludePatterns.Add(new ExcludePattern { Pattern = pattern });
        }

        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }

    public async Task DeleteFolderAsync(int id, CancellationToken ct = default)
    {
        await db.ExcludePatterns.Where(p => p.SearchFolderId == id).ExecuteDeleteAsync(ct);
        await db.GroupSearchFolders.Where(g => g.SearchFolderId == id).ExecuteDeleteAsync(ct);
        await db.SearchFolders.Where(f => f.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<GroupSummary>> ListGroupsAsync(CancellationToken ct = default) =>
        await db.Groups.AsNoTracking().OrderBy(g => g.Name)
            .Select(g => new GroupSummary(g.Id, g.Name, g.Description, g.UserGroups.Count, g.GroupSearchFolders.Count, g.IsDefault))
            .ToListAsync(ct);

    public async Task<PagedResult<GroupSummary>> ListGroupsPageAsync(
        int page, int pageSize, GroupListQuery? query = null, CancellationToken ct = default)
    {
        query ??= new GroupListQuery();
        var groups = db.Groups.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = SqlLike.Contains(query.Search);
            groups = groups.Where(g => EF.Functions.Like(g.Name, pattern, SqlLike.Escape)
                || (g.Description != null && EF.Functions.Like(g.Description, pattern, SqlLike.Escape)));
        }

        var total = await groups.CountAsync(ct);
        page = PagedResult<GroupSummary>.ClampPage(page, total, pageSize);
        var ordered = (query.Sort, query.Descending) switch
        {
            (GroupSortKeys.Description, false) => groups.OrderBy(g => g.Description),
            (GroupSortKeys.Description, true) => groups.OrderByDescending(g => g.Description),
            (GroupSortKeys.Members, false) => groups.OrderBy(g => g.UserGroups.Count),
            (GroupSortKeys.Members, true) => groups.OrderByDescending(g => g.UserGroups.Count),
            (GroupSortKeys.Folders, false) => groups.OrderBy(g => g.GroupSearchFolders.Count),
            (GroupSortKeys.Folders, true) => groups.OrderByDescending(g => g.GroupSearchFolders.Count),
            (GroupSortKeys.Name, true) => groups.OrderByDescending(g => g.Name),
            _ => groups.OrderBy(g => g.Name),
        };
        var items = await ordered.ThenBy(g => g.Name).ThenBy(g => g.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(g => new GroupSummary(g.Id, g.Name, g.Description, g.UserGroups.Count, g.GroupSearchFolders.Count, g.IsDefault))
            .ToListAsync(ct);
        return new PagedResult<GroupSummary>(items, page, pageSize, total);
    }

    public async Task<GroupEdit?> GetGroupAsync(int id, CancellationToken ct = default) =>
        await db.Groups.AsNoTracking().Where(g => g.Id == id)
            .Select(g => new GroupEdit
            {
                Id = g.Id,
                Name = g.Name,
                Description = g.Description,
                IsDefault = g.IsDefault,
                UserIds = g.UserGroups.Select(u => u.UserId).ToList(),
                FolderIds = g.GroupSearchFolders.Select(f => f.SearchFolderId).ToList(),
            })
            .SingleOrDefaultAsync(ct);

    public async Task<OperationResult> SaveGroupAsync(GroupEdit group, CancellationToken ct = default)
    {
        var name = group.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return OperationResult.Failure("Error.Group.NameRequired");
        }

        if (await db.Groups.AnyAsync(g => g.Name == name && g.Id != group.Id, ct))
        {
            return OperationResult.Failure("Error.Group.NameDuplicate");
        }

        Group entity;
        if (group.Id == 0)
        {
            entity = new Group();
            db.Groups.Add(entity);
        }
        else
        {
            var found = await db.Groups
                .Include(g => g.UserGroups)
                .Include(g => g.GroupSearchFolders)
                .SingleOrDefaultAsync(g => g.Id == group.Id, ct);
            if (found is null)
            {
                return OperationResult.Failure("Error.Group.NotFound");
            }

            entity = found;
            entity.UserGroups.Clear();
            entity.GroupSearchFolders.Clear();
        }

        if (!entity.IsDefault)
        {
            entity.Name = name;
        }

        entity.Description = string.IsNullOrWhiteSpace(group.Description) ? null : group.Description.Trim();

        var validUserIds = await db.Users.Where(u => group.UserIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        var validFolderIds = await db.SearchFolders.Where(f => group.FolderIds.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct);
        entity.UserGroups.AddRange(validUserIds.Select(id => new UserGroup { UserId = id }));
        entity.GroupSearchFolders.AddRange(validFolderIds.Select(id => new GroupSearchFolder { SearchFolderId = id }));

        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }

    public async Task<OperationResult> DeleteGroupAsync(int id, CancellationToken ct = default)
    {
        if (await db.Groups.AnyAsync(g => g.Id == id && g.IsDefault, ct))
        {
            return OperationResult.Failure("Error.Group.DefaultNotDeletable");
        }

        await db.UserGroups.Where(g => g.GroupId == id).ExecuteDeleteAsync(ct);
        await db.GroupSearchFolders.Where(g => g.GroupId == id).ExecuteDeleteAsync(ct);
        await db.Groups.Where(g => g.Id == id).ExecuteDeleteAsync(ct);
        return OperationResult.Success;
    }

    public Task<int> GetDefaultPageSizeAsync(CancellationToken ct = default) =>
        GetPageSizeSettingAsync(SystemSetting.PageSizeKey, SystemSetting.DefaultPageSize, ct);

    public Task<OperationResult> SetDefaultPageSizeAsync(int pageSize, CancellationToken ct = default) =>
        SetPageSizeSettingAsync(SystemSetting.PageSizeKey, pageSize, ct);

    public Task<int> GetLogPageSizeAsync(CancellationToken ct = default) =>
        GetPageSizeSettingAsync(SystemSetting.LogPageSizeKey, SystemSetting.DefaultLogPageSize, ct);

    public Task<OperationResult> SetLogPageSizeAsync(int pageSize, CancellationToken ct = default) =>
        SetPageSizeSettingAsync(SystemSetting.LogPageSizeKey, pageSize, ct);

    public Task<int> GetAdminListPageSizeAsync(CancellationToken ct = default) =>
        GetPageSizeSettingAsync(SystemSetting.AdminListPageSizeKey, SystemSetting.DefaultListPageSize, ct);

    public Task<OperationResult> SetAdminListPageSizeAsync(int pageSize, CancellationToken ct = default) =>
        SetPageSizeSettingAsync(SystemSetting.AdminListPageSizeKey, pageSize, ct);

    private async Task<int> GetPageSizeSettingAsync(string key, int fallback, CancellationToken ct)
    {
        var value = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(ct);
        return int.TryParse(value, out var size) && PageSizeLimits.IsValid(size) ? size : fallback;
    }

    private async Task<OperationResult> SetPageSizeSettingAsync(string key, int pageSize, CancellationToken ct)
    {
        if (!PageSizeLimits.IsValid(pageSize))
        {
            return OperationResult.Failure("Error.PageSize.Range", PageSizeLimits.Min, PageSizeLimits.Max);
        }

        var setting = await db.SystemSettings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = pageSize.ToString() });
        }
        else
        {
            setting.Value = pageSize.ToString();
        }

        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }

    public async Task<IncrementalSearchSettings> GetIncrementalSearchAsync(CancellationToken ct = default)
    {
        string[] keys =
        [
            SystemSetting.IncrementalSearchEnabledKey, SystemSetting.IncrementalSearchMinCharsKey, SystemSetting.IncrementalSearchDelayMsKey,
        ];
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var fallback = IncrementalSearchSettings.Default;
        int Int(string key, int defaultValue) =>
            values.TryGetValue(key, out var text) && int.TryParse(text, CultureInfo.InvariantCulture, out var number) ? number : defaultValue;

        var settings = new IncrementalSearchSettings(
            values.TryGetValue(SystemSetting.IncrementalSearchEnabledKey, out var enabled) && bool.TryParse(enabled, out var e)
                ? e : fallback.Enabled,
            Int(SystemSetting.IncrementalSearchMinCharsKey, fallback.MinChars),
            Int(SystemSetting.IncrementalSearchDelayMsKey, fallback.DelayMs));

        // DB が手で書き換えられて範囲外になっていても、検索画面が壊れないようにする。
        return settings.IsValid ? settings : fallback with { Enabled = settings.Enabled };
    }

    public async Task<OperationResult> SetIncrementalSearchAsync(IncrementalSearchSettings settings, CancellationToken ct = default)
    {
        if (settings.MinChars is < IncrementalSearchSettings.MinCharsMin or > IncrementalSearchSettings.MinCharsMax)
        {
            return OperationResult.Failure(
                "Error.IncrementalSearch.MinCharsRange", IncrementalSearchSettings.MinCharsMin, IncrementalSearchSettings.MinCharsMax);
        }

        if (settings.DelayMs is < IncrementalSearchSettings.DelayMsMin or > IncrementalSearchSettings.DelayMsMax)
        {
            return OperationResult.Failure(
                "Error.IncrementalSearch.DelayRange", IncrementalSearchSettings.DelayMsMin, IncrementalSearchSettings.DelayMsMax);
        }

        await SetValueAsync(SystemSetting.IncrementalSearchEnabledKey, settings.Enabled.ToString(), ct);
        await SetValueAsync(SystemSetting.IncrementalSearchMinCharsKey, settings.MinChars.ToString(CultureInfo.InvariantCulture), ct);
        await SetValueAsync(SystemSetting.IncrementalSearchDelayMsKey, settings.DelayMs.ToString(CultureInfo.InvariantCulture), ct);
        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }

    public async Task<ExportSettings> GetExportAsync(CancellationToken ct = default)
    {
        string[] keys = [SystemSetting.ExportAllowAllUsersKey, SystemSetting.ExportMaxRowsKey];
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var fallback = ExportSettings.Default;
        var settings = new ExportSettings(
            values.TryGetValue(SystemSetting.ExportAllowAllUsersKey, out var allow) && bool.TryParse(allow, out var a) ? a : fallback.AllowAllUsers,
            values.TryGetValue(SystemSetting.ExportMaxRowsKey, out var rows) && int.TryParse(rows, CultureInfo.InvariantCulture, out var r)
                ? r : fallback.MaxRows);

        // DB が手で書き換えられて範囲外になっていても、エクスポートが壊れないようにする。
        return settings.IsValid ? settings : fallback with { AllowAllUsers = settings.AllowAllUsers };
    }

    public async Task<OperationResult> SetExportAsync(ExportSettings settings, CancellationToken ct = default)
    {
        if (!settings.IsValid)
        {
            return OperationResult.Failure("Error.Export.MaxRowsRange", ExportSettings.MaxRowsMin, ExportSettings.MaxRowsMax);
        }

        await SetValueAsync(SystemSetting.ExportAllowAllUsersKey, settings.AllowAllUsers.ToString(), ct);
        await SetValueAsync(SystemSetting.ExportMaxRowsKey, settings.MaxRows.ToString(CultureInfo.InvariantCulture), ct);
        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }

    private async Task SetValueAsync(string key, string value, CancellationToken ct)
    {
        var setting = await db.SystemSettings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }
    }

    public async Task<IReadOnlyList<string>> GetDefaultExcludePatternsAsync(CancellationToken ct = default)
    {
        var value = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSetting.DefaultExcludePatternsKey).Select(s => s.Value).SingleOrDefaultAsync(ct);
        return value is null ? ExcludePatternRules.BuiltIn : ExcludePatternRules.Split(value);
    }

    public async Task<bool> IsDefaultExcludePatternsCustomizedAsync(CancellationToken ct = default) =>
        await db.SystemSettings.AsNoTracking().AnyAsync(s => s.Key == SystemSetting.DefaultExcludePatternsKey, ct);

    public async Task<OperationResult> SetDefaultExcludePatternsAsync(IEnumerable<string> patterns, CancellationToken ct = default)
    {
        var (normalized, errors) = ExcludePatternRules.Normalize(patterns);
        if (errors.Count > 0)
        {
            return OperationResult.Failure(errors);
        }

        // 空の一覧は「何も除外しない」という設定。空文字は改行区切りの 0 件として保存する。
        var value = string.Join('\n', normalized);
        var setting = await db.SystemSettings.SingleOrDefaultAsync(s => s.Key == SystemSetting.DefaultExcludePatternsKey, ct);
        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSetting.DefaultExcludePatternsKey, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }

    public async Task ResetDefaultExcludePatternsAsync(CancellationToken ct = default) =>
        await db.SystemSettings.Where(s => s.Key == SystemSetting.DefaultExcludePatternsKey).ExecuteDeleteAsync(ct);
}
