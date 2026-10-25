using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Identity;

public sealed class ActiveDirectorySettingsService(AppDbContext db, IDataProtectionProvider dataProtection) : IActiveDirectorySettingsService
{
    /// <summary>確認用アカウントのパスワードを暗号化する目的（他の用途の保護データと取り違えないため）。</summary>
    internal const string BindPasswordPurpose = "NowFound.ActiveDirectory.BindPassword";

    private static readonly string[] Keys =
    [
        SystemSetting.AdEnabledKey, SystemSetting.AdServerKey, SystemSetting.AdPortKey,
        SystemSetting.AdSecurityKey, SystemSetting.AdDomainKey,
        SystemSetting.AdBindUserKey, SystemSetting.AdSearchBaseKey,
    ];

    private IDataProtector Protector => dataProtection.CreateProtector(BindPasswordPurpose);

    public async Task<ActiveDirectorySettings> GetAsync(CancellationToken ct = default)
    {
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => Keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var fallback = ActiveDirectorySettings.Default;
        var settings = new ActiveDirectorySettings(
            values.TryGetValue(SystemSetting.AdEnabledKey, out var enabled) && bool.TryParse(enabled, out var e) && e,
            values.GetValueOrDefault(SystemSetting.AdServerKey, fallback.Server),
            values.TryGetValue(SystemSetting.AdPortKey, out var port) && int.TryParse(port, CultureInfo.InvariantCulture, out var p)
                ? p : fallback.Port,
            values.TryGetValue(SystemSetting.AdSecurityKey, out var security) && Enum.TryParse<DirectorySecurity>(security, out var s)
                && Enum.IsDefined(s) ? s : fallback.Security,
            values.GetValueOrDefault(SystemSetting.AdDomainKey, fallback.Domain),
            values.GetValueOrDefault(SystemSetting.AdBindUserKey, fallback.BindUser),
            values.GetValueOrDefault(SystemSetting.AdSearchBaseKey, fallback.SearchBase));

        // DB が手で書き換えられて不正な値になっていても、ログインの検証に使わない（無効として扱う）。
        return ActiveDirectoryRules.Validate(settings).Succeeded ? settings : fallback;
    }

    public async Task<OperationResult> SetAsync(
        ActiveDirectorySettings settings, string? bindPassword = null, CancellationToken ct = default, bool requireBindPassword = true)
    {
        settings = settings with
        {
            Server = settings.Server.Trim(),
            Domain = settings.Domain.Trim(),
            BindUser = settings.BindUser.Trim(),
            SearchBase = settings.SearchBase.Trim(),
        };
        var validation = ActiveDirectoryRules.Validate(settings);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var hasStored = await HasBindPasswordAsync(ct);
        if (requireBindPassword && settings.BindUser.Length > 0 && string.IsNullOrEmpty(bindPassword) && !hasStored)
        {
            return OperationResult.Failure("Error.Ad.BindPasswordRequired");
        }

        await SetValueAsync(SystemSetting.AdEnabledKey, settings.Enabled.ToString(), ct);
        await SetValueAsync(SystemSetting.AdServerKey, settings.Server, ct);
        await SetValueAsync(SystemSetting.AdPortKey, settings.Port.ToString(CultureInfo.InvariantCulture), ct);
        await SetValueAsync(SystemSetting.AdSecurityKey, settings.Security.ToString(), ct);
        await SetValueAsync(SystemSetting.AdDomainKey, settings.Domain, ct);
        await SetValueAsync(SystemSetting.AdBindUserKey, settings.BindUser, ct);
        await SetValueAsync(SystemSetting.AdSearchBaseKey, settings.SearchBase, ct);

        if (settings.BindUser.Length == 0)
        {
            // 確認用アカウントを外したら、パスワードも残さない。
            await RemoveValueAsync(SystemSetting.AdBindPasswordKey, ct);
        }
        else if (!string.IsNullOrEmpty(bindPassword))
        {
            await SetValueAsync(SystemSetting.AdBindPasswordKey, Protector.Protect(bindPassword), ct);
        }

        await db.SaveChangesAsync(ct);
        return OperationResult.Success;
    }

    public Task<bool> HasBindPasswordAsync(CancellationToken ct = default) =>
        db.SystemSettings.AsNoTracking().AnyAsync(s => s.Key == SystemSetting.AdBindPasswordKey && s.Value != string.Empty, ct);

    public async Task<string?> GetBindPasswordAsync(CancellationToken ct = default)
    {
        var protectedValue = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSetting.AdBindPasswordKey)
            .Select(s => s.Value)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            return Protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            // 暗号鍵が失われた（再インストールや別サーバーへの移行など）。管理者がパスワードを入力し直す。
            return null;
        }
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

    private async Task RemoveValueAsync(string key, CancellationToken ct)
    {
        var setting = await db.SystemSettings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (setting is not null)
        {
            db.SystemSettings.Remove(setting);
        }
    }
}
