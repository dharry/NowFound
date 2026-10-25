using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Identity;

/// <summary>
/// 現在のアカウントポリシー（最小文字数・複雑性・過去のパスワードの再利用禁止）でパスワードを検証する。
/// Identity 標準の規則は無効にしてあるので、これが唯一の規則。
/// </summary>
public sealed class PolicyPasswordValidator(
    IAccountPolicyStore policyStore,
    IdentityErrorDescriber describer,
    AppDbContext db,
    IPasswordHasher<AppUser> hasher)
    : IPasswordValidator<AppUser>
{
    public async Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        var policy = policyStore.Current;
        var errors = new List<IdentityError>();
        foreach (var violation in PasswordRules.Validate(password ?? string.Empty, policy))
        {
            errors.Add(violation switch
            {
                PasswordViolation.TooShort => describer.PasswordTooShort(policy.PasswordMinLength),
                _ => Describer.PasswordComplexity(),
            });
        }

        if (policy.PasswordHistoryCount > 0 && !string.IsNullOrEmpty(password) && await WasUsedRecentlyAsync(user, password, policy))
        {
            errors.Add(Describer.PasswordReused(policy.PasswordHistoryCount));
        }

        return errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]);
    }

    private AppIdentityErrorDescriber Describer => (describer as AppIdentityErrorDescriber) ?? new AppIdentityErrorDescriber();

    /// <summary>直近の <see cref="AccountPolicy.PasswordHistoryCount"/> 件（現在のパスワードを含む）のどれかと同じか。</summary>
    private async Task<bool> WasUsedRecentlyAsync(AppUser user, string password, AccountPolicy policy)
    {
        var hashes = await db.PasswordHistories.AsNoTracking()
            .Where(h => h.UserId == user.Id)
            .OrderByDescending(h => h.CreatedAt).ThenByDescending(h => h.Id)
            .Take(policy.PasswordHistoryCount)
            .Select(h => h.PasswordHash)
            .ToListAsync();

        // 履歴を持たない既存ユーザーでも、現在のパスワードは必ず比較する。
        if (!string.IsNullOrEmpty(user.PasswordHash) && !hashes.Contains(user.PasswordHash))
        {
            hashes.Add(user.PasswordHash);
        }

        return hashes.Any(hash => hasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed);
    }
}
