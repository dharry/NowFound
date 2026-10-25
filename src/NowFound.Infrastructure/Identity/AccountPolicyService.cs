using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Identity;

/// <summary>現在のアカウントポリシーの保持。起動時と保存時に <see cref="AccountPolicyService"/> が更新する。</summary>
public sealed class AccountPolicyStore : IAccountPolicyStore
{
    private volatile AccountPolicy _current = AccountPolicy.Default;

    public AccountPolicy Current => _current;

    public void Update(AccountPolicy policy) => _current = policy;
}

public sealed class AccountPolicyService(AppDbContext db, AccountPolicyStore store) : IAccountPolicyService
{
    public async Task<AccountPolicy> GetAsync(CancellationToken ct = default)
    {
        string[] keys =
        [
            SystemSetting.PasswordMinLengthKey, SystemSetting.PasswordComplexityKey, SystemSetting.MaxFailedAttemptsKey,
            SystemSetting.PasswordMaxAgeDaysKey, SystemSetting.PasswordHistoryCountKey,
        ];
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var fallback = AccountPolicy.Default;
        int Int(string key, int defaultValue) =>
            values.TryGetValue(key, out var text) && int.TryParse(text, CultureInfo.InvariantCulture, out var number) ? number : defaultValue;

        var policy = new AccountPolicy(
            Int(SystemSetting.PasswordMinLengthKey, fallback.PasswordMinLength),
            values.TryGetValue(SystemSetting.PasswordComplexityKey, out var complexity) && bool.TryParse(complexity, out var c)
                ? c : fallback.RequireComplexity,
            Int(SystemSetting.MaxFailedAttemptsKey, fallback.MaxFailedAttempts),
            Int(SystemSetting.PasswordMaxAgeDaysKey, fallback.PasswordMaxAgeDays),
            Int(SystemSetting.PasswordHistoryCountKey, fallback.PasswordHistoryCount));

        // DB が手で書き換えられて範囲外になっていても、ポリシーが壊れないようにする。
        policy = policy.IsValid ? policy : fallback;
        store.Update(policy);
        return policy;
    }

    public async Task<OperationResult> SetAsync(AccountPolicy policy, CancellationToken ct = default)
    {
        if (policy.PasswordMinLength is < AccountPolicy.MinLengthMin or > AccountPolicy.MinLengthMax)
        {
            return OperationResult.Failure("Error.Policy.MinLengthRange", AccountPolicy.MinLengthMin, AccountPolicy.MinLengthMax);
        }

        if (policy.MaxFailedAttempts is < 0 or > AccountPolicy.MaxFailedAttemptsMax)
        {
            return OperationResult.Failure("Error.Policy.MaxFailedRange", 0, AccountPolicy.MaxFailedAttemptsMax);
        }

        if (policy.PasswordMaxAgeDays is < 0 or > AccountPolicy.MaxAgeDaysMax)
        {
            return OperationResult.Failure("Error.Policy.MaxAgeRange", 0, AccountPolicy.MaxAgeDaysMax);
        }

        if (policy.PasswordHistoryCount is < 0 or > AccountPolicy.HistoryCountMax)
        {
            return OperationResult.Failure("Error.Policy.HistoryRange", 0, AccountPolicy.HistoryCountMax);
        }

        await SetValueAsync(SystemSetting.PasswordMinLengthKey, policy.PasswordMinLength.ToString(CultureInfo.InvariantCulture), ct);
        await SetValueAsync(SystemSetting.PasswordComplexityKey, policy.RequireComplexity.ToString(), ct);
        await SetValueAsync(SystemSetting.MaxFailedAttemptsKey, policy.MaxFailedAttempts.ToString(CultureInfo.InvariantCulture), ct);
        await SetValueAsync(SystemSetting.PasswordMaxAgeDaysKey, policy.PasswordMaxAgeDays.ToString(CultureInfo.InvariantCulture), ct);
        await SetValueAsync(SystemSetting.PasswordHistoryCountKey, policy.PasswordHistoryCount.ToString(CultureInfo.InvariantCulture), ct);
        await db.SaveChangesAsync(ct);

        store.Update(policy);
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
}
