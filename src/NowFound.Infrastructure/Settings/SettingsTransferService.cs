using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NowFound.Application;
using NowFound.Application.FileKinds;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Settings;

public sealed class SettingsTransferService(
    AppDbContext db,
    IAdminSettingsService settings,
    IAccountPolicyService accountPolicy,
    IActiveDirectorySettingsService activeDirectory,
    IFileKindService fileKinds) : ISettingsTransferService
{
    internal const string FoldersFormat = "nowfound.folders";
    internal const string SystemSettingsFormat = "nowfound.system-settings";
    internal const string FileKindsFormat = "nowfound.file-kinds";
    internal const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // 日本語のパスや名前を \uXXXX にせず、そのまま読める形で書き出す。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<string> ExportFoldersAsync(CancellationToken ct = default)
    {
        var folders = await db.SearchFolders.AsNoTracking().OrderBy(f => f.Name).ThenBy(f => f.Id)
            .Select(f => new FolderDto
            {
                Name = f.Name,
                Path = f.Path,
                ExcludeHidden = f.ExcludeHidden,
                IsShared = f.IsShared,
                ShareName = f.ShareName,
                ExcludePatterns = f.ExcludePatterns.OrderBy(p => p.Id).Select(p => p.Pattern).ToList(),
            })
            .ToListAsync(ct);
        return JsonSerializer.Serialize(new FoldersFile { Format = FoldersFormat, Version = CurrentVersion, Folders = [.. folders] }, JsonOptions);
    }

    public async Task<ImportResult> ImportFoldersAsync(string json, CancellationToken ct = default)
    {
        var (file, error) = Parse<FoldersFile>(json, FoldersFormat);
        if (file is null)
        {
            return new(error!);
        }

        var folders = file.Folders ?? [];
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        var updated = 0;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var (folder, index) in folders.Select((f, i) => (f, i + 1)))
        {
            var label = string.IsNullOrWhiteSpace(folder?.Path) ? $"#{index}" : folder.Path.Trim();
            if (folder is null)
            {
                return await RollbackAsync(transaction, OperationResult.Failure("Error.Transfer.FolderInvalid", label), ct);
            }

            var (patterns, patternErrors) = ExcludePatternRules.Normalize(folder.ExcludePatterns ?? []);
            if (patternErrors.Count > 0)
            {
                return await RollbackAsync(transaction, FolderFailure(label, patternErrors), ct);
            }

            var path = NormalizePath(folder.Path);
            if (path.Length > 0 && !seenPaths.Add(path))
            {
                return await RollbackAsync(transaction, OperationResult.Failure("Error.Transfer.FolderDuplicate", path), ct);
            }

            var existingId = await db.SearchFolders.AsNoTracking()
                .Where(f => f.Path == path).Select(f => (int?)f.Id).FirstOrDefaultAsync(ct);
            var result = await settings.SaveFolderAsync(new FolderEdit
            {
                Id = existingId ?? 0,
                Name = folder.Name ?? string.Empty,
                Path = folder.Path ?? string.Empty,
                ExcludeHidden = folder.ExcludeHidden ?? true,
                IsShared = folder.IsShared ?? false,
                ShareName = folder.ShareName,
                ExcludePatterns = patterns,
            }, ct);
            if (!result.Succeeded)
            {
                return await RollbackAsync(transaction, FolderFailure(label, result.Errors), ct);
            }

            if (existingId is null)
            {
                created++;
            }
            else
            {
                updated++;
            }
        }

        await transaction.CommitAsync(ct);
        return new(OperationResult.Success, created, updated);
    }

    public async Task<string> ExportSystemSettingsAsync(CancellationToken ct = default)
    {
        var incremental = await settings.GetIncrementalSearchAsync(ct);
        var policy = await accountPolicy.GetAsync(ct);
        var ad = await activeDirectory.GetAsync(ct);
        var file = new SystemSettingsFile
        {
            Format = SystemSettingsFormat,
            Version = CurrentVersion,
            PageSize = await settings.GetDefaultPageSizeAsync(ct),
            LogPageSize = await settings.GetLogPageSizeAsync(ct),
            AdminListPageSize = await settings.GetAdminListPageSizeAsync(ct),
            IncrementalSearch = new IncrementalDto { Enabled = incremental.Enabled, MinChars = incremental.MinChars, DelayMs = incremental.DelayMs },
            DefaultExcludePatterns = new ExcludeDefaultsDto
            {
                Customized = await settings.IsDefaultExcludePatternsCustomizedAsync(ct),
                Patterns = [.. await settings.GetDefaultExcludePatternsAsync(ct)],
            },
            AccountPolicy = new PolicyDto
            {
                PasswordMinLength = policy.PasswordMinLength,
                RequireComplexity = policy.RequireComplexity,
                MaxFailedAttempts = policy.MaxFailedAttempts,
                PasswordMaxAgeDays = policy.PasswordMaxAgeDays,
                PasswordHistoryCount = policy.PasswordHistoryCount,
            },
            ActiveDirectory = new AdDto
            {
                Enabled = ad.Enabled,
                Server = ad.Server,
                Port = ad.Port,
                Security = ad.Security,
                Domain = ad.Domain,
                BindUser = ad.BindUser,
                SearchBase = ad.SearchBase,
            },
        };
        return JsonSerializer.Serialize(file, JsonOptions);
    }

    public async Task<ImportResult> ImportSystemSettingsAsync(string json, CancellationToken ct = default)
    {
        var (file, error) = Parse<SystemSettingsFile>(json, SystemSettingsFormat);
        if (file is null)
        {
            return new(error!);
        }

        // 反映の前に、取り込む内容をすべて検証する。
        var errors = new List<LocalizedMessage>();
        foreach (var (name, value) in new[]
            {
                ("pageSize", file.PageSize), ("logPageSize", file.LogPageSize), ("adminListPageSize", file.AdminListPageSize),
            })
        {
            if (value is { } size && !PageSizeLimits.IsValid(size))
            {
                errors.Add(new("Error.Transfer.ValueInvalid", name));
            }
        }

        IncrementalSearchSettings? incremental = null;
        if (file.IncrementalSearch is { } i)
        {
            incremental = new(i.Enabled ?? IncrementalSearchSettings.Default.Enabled, i.MinChars ?? 0, i.DelayMs ?? 0);
            if (!incremental.IsValid)
            {
                errors.Add(new("Error.Transfer.ValueInvalid", "incrementalSearch"));
            }
        }

        List<string>? excludePatterns = null;
        var resetExclude = false;
        if (file.DefaultExcludePatterns is { } e)
        {
            if (e.Customized == false)
            {
                resetExclude = true;
            }
            else
            {
                var (patterns, patternErrors) = ExcludePatternRules.Normalize(e.Patterns ?? []);
                errors.AddRange(patternErrors);
                excludePatterns = patterns;
            }
        }

        AccountPolicy? policy = null;
        if (file.AccountPolicy is { } p)
        {
            var current = AccountPolicy.Default;
            policy = new AccountPolicy(
                p.PasswordMinLength ?? current.PasswordMinLength,
                p.RequireComplexity ?? current.RequireComplexity,
                p.MaxFailedAttempts ?? current.MaxFailedAttempts,
                p.PasswordMaxAgeDays ?? current.PasswordMaxAgeDays,
                p.PasswordHistoryCount ?? current.PasswordHistoryCount);
            if (!policy.IsValid)
            {
                errors.Add(new("Error.Transfer.ValueInvalid", "accountPolicy"));
            }
        }

        ActiveDirectorySettings? ad = null;
        if (file.ActiveDirectory is { } a)
        {
            var fallback = ActiveDirectorySettings.Default;
            ad = new ActiveDirectorySettings(
                a.Enabled ?? false,
                a.Server?.Trim() ?? string.Empty,
                a.Port ?? fallback.Port,
                a.Security ?? fallback.Security,
                a.Domain?.Trim() ?? string.Empty,
                a.BindUser?.Trim() ?? string.Empty,
                a.SearchBase?.Trim() ?? string.Empty);
            var validation = ActiveDirectoryRules.Validate(ad);
            errors.AddRange(validation.Errors);
        }

        if (errors.Count > 0)
        {
            return new(OperationResult.Failure(errors));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var results = new List<OperationResult>();
            if (file.PageSize is { } pageSize)
            {
                results.Add(await settings.SetDefaultPageSizeAsync(pageSize, ct));
            }

            if (file.LogPageSize is { } logPageSize)
            {
                results.Add(await settings.SetLogPageSizeAsync(logPageSize, ct));
            }

            if (file.AdminListPageSize is { } adminListPageSize)
            {
                results.Add(await settings.SetAdminListPageSizeAsync(adminListPageSize, ct));
            }

            if (incremental is not null)
            {
                results.Add(await settings.SetIncrementalSearchAsync(incremental, ct));
            }

            if (resetExclude)
            {
                await settings.ResetDefaultExcludePatternsAsync(ct);
            }
            else if (excludePatterns is not null)
            {
                results.Add(await settings.SetDefaultExcludePatternsAsync(excludePatterns, ct));
            }

            if (policy is not null)
            {
                results.Add(await accountPolicy.SetAsync(policy, ct));
            }

            if (ad is not null)
            {
                results.Add(await activeDirectory.SetAsync(ad, null, ct, requireBindPassword: false));
            }

            if (results.FirstOrDefault(r => !r.Succeeded) is { } failed)
            {
                return await RollbackAsync(transaction, failed, ct);
            }

            await transaction.CommitAsync(ct);

            // パスワードはファイルに含まれないため、確認用アカウントを使う場合は管理者が再設定する。
            var bindPasswordNeeded = ad is { BindUser.Length: > 0 } && !await activeDirectory.HasBindPasswordAsync(ct);
            return new(OperationResult.Success, BindPasswordNeeded: bindPasswordNeeded);
        }
        catch
        {
            // アカウントポリシーはメモリ上にも保持しているため、取り消した内容に合わせて読み直す。
            db.ChangeTracker.Clear();
            await accountPolicy.GetAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<string> ExportFileKindsAsync(CancellationToken ct = default)
    {
        var file = new FileKindsFile
        {
            Format = FileKindsFormat,
            Version = CurrentVersion,
            Customized = await fileKinds.IsCustomizedAsync(ct),
            Kinds =
            [
                .. (await fileKinds.GetDefinitionsAsync(ct)).Select(d => new FileKindDto
                {
                    Key = d.Key,
                    Label = d.Label,
                    Icon = d.Icon,
                    Extensions = [.. d.Extensions],
                }),
            ],
        };
        return JsonSerializer.Serialize(file, JsonOptions);
    }

    public async Task<ImportResult> ImportFileKindsAsync(string json, CancellationToken ct = default)
    {
        var (file, error) = Parse<FileKindsFile>(json, FileKindsFormat);
        if (file is null)
        {
            return new(error!);
        }

        if (file.Customized == false)
        {
            await fileKinds.ResetAsync(ct);
            return new(OperationResult.Success);
        }

        if (file.Kinds is not { } kinds || kinds.Any(k => k is null))
        {
            return new(OperationResult.Failure("Error.Transfer.ValueInvalid", "kinds"));
        }

        // 検証と全体の置き換え（トランザクション）は FileKindService.SaveAsync が行う。
        var result = await fileKinds.SaveAsync(
            [.. kinds.Select(k => new FileKindEdit
            {
                Key = k!.Key,
                Label = k.Label,
                Icon = k.Icon,
                Extensions = string.Join(',', k.Extensions ?? []),
            })], ct);
        return new(result);
    }

    private async Task<ImportResult> RollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, OperationResult failure, CancellationToken ct)
    {
        await transaction.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        // 途中までアカウントポリシーを更新していた場合に備えて、メモリ上の値をデータベースに合わせる。
        await accountPolicy.GetAsync(ct);
        return new(failure);
    }

    private static OperationResult FolderFailure(string label, IEnumerable<LocalizedMessage> inner) =>
        OperationResult.Failure([new LocalizedMessage("Error.Transfer.FolderInvalid", label), .. inner]);

    /// <summary><see cref="AdminSettingsService.SaveFolderAsync"/> が保存するパスと同じ形にする（既存フォルダの照合用）。</summary>
    private static string NormalizePath(string? raw)
    {
        var path = raw?.Trim() ?? string.Empty;
        return path.Length > 3 ? path.TrimEnd('\\', '/') : path;
    }

    private static (T? File, OperationResult? Error) Parse<T>(string json, string format) where T : TransferFile
    {
        T? file;
        try
        {
            file = JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return (null, OperationResult.Failure("Error.Transfer.InvalidJson"));
        }

        if (file is null || file.Format != format)
        {
            return (null, OperationResult.Failure("Error.Transfer.WrongFormat"));
        }

        return file.Version == CurrentVersion
            ? (file, null)
            : (null, OperationResult.Failure("Error.Transfer.UnsupportedVersion", file.Version, CurrentVersion));
    }

    private abstract class TransferFile
    {
        public string? Format { get; set; }
        public int Version { get; set; }
    }

    private sealed class FoldersFile : TransferFile
    {
        public List<FolderDto?>? Folders { get; set; }
    }

    private sealed class FolderDto
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public bool? ExcludeHidden { get; set; }
        public bool? IsShared { get; set; }
        public string? ShareName { get; set; }
        public List<string>? ExcludePatterns { get; set; }
    }

    private sealed class FileKindsFile : TransferFile
    {
        /// <summary>false なら組み込みの既定値に戻す（<see cref="Kinds"/> は使わない）。</summary>
        public bool? Customized { get; set; }
        public List<FileKindDto?>? Kinds { get; set; }
    }

    private sealed class FileKindDto
    {
        public string? Key { get; set; }
        public string? Label { get; set; }
        public string? Icon { get; set; }
        public List<string>? Extensions { get; set; }
    }

    private sealed class SystemSettingsFile : TransferFile
    {
        public int? PageSize { get; set; }
        public int? LogPageSize { get; set; }
        public int? AdminListPageSize { get; set; }
        public IncrementalDto? IncrementalSearch { get; set; }
        public ExcludeDefaultsDto? DefaultExcludePatterns { get; set; }
        public PolicyDto? AccountPolicy { get; set; }
        public AdDto? ActiveDirectory { get; set; }
    }

    private sealed class IncrementalDto
    {
        public bool? Enabled { get; set; }
        public int? MinChars { get; set; }
        public int? DelayMs { get; set; }
    }

    private sealed class ExcludeDefaultsDto
    {
        /// <summary>false なら組み込みの既定値に戻す（<see cref="Patterns"/> は使わない）。</summary>
        public bool? Customized { get; set; }
        public List<string>? Patterns { get; set; }
    }

    private sealed class PolicyDto
    {
        public int? PasswordMinLength { get; set; }
        public bool? RequireComplexity { get; set; }
        public int? MaxFailedAttempts { get; set; }
        public int? PasswordMaxAgeDays { get; set; }
        public int? PasswordHistoryCount { get; set; }
    }

    private sealed class AdDto
    {
        public bool? Enabled { get; set; }
        public string? Server { get; set; }
        public int? Port { get; set; }
        public DirectorySecurity? Security { get; set; }
        public string? Domain { get; set; }
        public string? BindUser { get; set; }
        public string? SearchBase { get; set; }
    }
}
