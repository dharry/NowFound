namespace NowFound.Application.Users;

/// <summary>アカウントポリシー（システム全体で 1 つ）。パスワードの最小文字数・複雑性・ログイン失敗でのロック回数。</summary>
/// <param name="PasswordMinLength">パスワードの最小文字数（<see cref="MinLengthMin"/>～<see cref="MinLengthMax"/>）。</param>
/// <param name="RequireComplexity">true の場合、大文字・小文字・数字・記号のうち <see cref="MinCategories"/> 種類以上を含める。</param>
/// <param name="MaxFailedAttempts">ログイン失敗でロックするまでの回数（0～<see cref="MaxFailedAttemptsMax"/>）。0 はロックしない。</param>
/// <param name="PasswordMaxAgeDays">パスワードの有効日数（0～<see cref="MaxAgeDaysMax"/>）。経過すると次のアクセスで変更を求める。0 は無期限。</param>
/// <param name="PasswordHistoryCount">再利用を禁止する直近のパスワードの数（0～<see cref="HistoryCountMax"/>、現在のパスワードを含む）。0 は禁止しない。</param>
public sealed record AccountPolicy(
    int PasswordMinLength, bool RequireComplexity, int MaxFailedAttempts, int PasswordMaxAgeDays = 0, int PasswordHistoryCount = 0)
{
    public const int MinLengthMin = 5;
    public const int MinLengthMax = 127;
    public const int MaxFailedAttemptsMax = 10;
    public const int MaxAgeDaysMax = 999;
    public const int HistoryCountMax = 24;

    /// <summary>複雑性を要求するときに含める文字の種類数（大文字・小文字・数字・記号のうち）。</summary>
    public const int MinCategories = 3;

    /// <summary>ロックされたときの自動解除までの時間。管理者はいつでも手動で解除できる。</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static AccountPolicy Default { get; } = new(8, true, 5);

    public bool IsValid =>
        PasswordMinLength is >= MinLengthMin and <= MinLengthMax
        && MaxFailedAttempts is >= 0 and <= MaxFailedAttemptsMax
        && PasswordMaxAgeDays is >= 0 and <= MaxAgeDaysMax
        && PasswordHistoryCount is >= 0 and <= HistoryCountMax;
}

public enum PasswordViolation
{
    TooShort,
    Complexity,
}

/// <summary>パスワードがアカウントポリシーを満たすかの判定。</summary>
public static class PasswordRules
{
    public static IReadOnlyList<PasswordViolation> Validate(string password, AccountPolicy policy)
    {
        var violations = new List<PasswordViolation>();
        if (password.Length < policy.PasswordMinLength)
        {
            violations.Add(PasswordViolation.TooShort);
        }

        if (policy.RequireComplexity && CountCategories(password) < AccountPolicy.MinCategories)
        {
            violations.Add(PasswordViolation.Complexity);
        }

        return violations;
    }

    /// <summary>含まれる文字の種類数（大文字・小文字・数字・記号）。記号は英数字以外すべて（空白や日本語を含む）。</summary>
    public static int CountCategories(string password)
    {
        var upper = false;
        var lower = false;
        var digit = false;
        var symbol = false;
        foreach (var c in password)
        {
            if (char.IsUpper(c))
            {
                upper = true;
            }
            else if (char.IsLower(c))
            {
                lower = true;
            }
            else if (char.IsDigit(c))
            {
                digit = true;
            }
            else
            {
                symbol = true;
            }
        }

        return (upper ? 1 : 0) + (lower ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0);
    }
}

/// <summary>現在のアカウントポリシーの保持（プロセス内で共有）。管理者が変更すると即時に反映される。</summary>
public interface IAccountPolicyStore
{
    AccountPolicy Current { get; }

    void Update(AccountPolicy policy);
}

/// <summary>アカウントポリシーの読み書き（DB）。保存すると <see cref="IAccountPolicyStore"/> も更新する。</summary>
public interface IAccountPolicyService
{
    /// <summary>DB から読み込み、<see cref="IAccountPolicyStore"/> にも反映する。未設定の項目は既定値。</summary>
    Task<AccountPolicy> GetAsync(CancellationToken ct = default);

    Task<OperationResult> SetAsync(AccountPolicy policy, CancellationToken ct = default);
}

public enum AccountStatus
{
    Active,
    Disabled,
    Expired,
}

/// <summary>ユーザーアカウントの有効・無効と有効期限の判定。</summary>
public static class AccountStatusRules
{
    /// <param name="expiresOn">有効期限（この日の終わりまで有効）。null は無期限。</param>
    /// <param name="today">サーバーのローカル日付。</param>
    public static AccountStatus Evaluate(bool isEnabled, DateOnly? expiresOn, DateOnly today) =>
        !isEnabled ? AccountStatus.Disabled
        : expiresOn is { } date && today > date ? AccountStatus.Expired
        : AccountStatus.Active;
}

/// <summary>パスワードの有効期間の判定。</summary>
public static class PasswordAgeRules
{
    /// <param name="maxAgeDays">有効日数。0 以下は無期限。</param>
    /// <param name="changedAtUtc">最後にパスワードを設定した日時（UTC）。不明（null）の場合は期限切れにしない。</param>
    public static bool IsExpired(int maxAgeDays, DateTime? changedAtUtc, DateTime nowUtc) =>
        maxAgeDays > 0 && changedAtUtc is { } changed && nowUtc > changed.AddDays(maxAgeDays);

    /// <summary>期限日時（UTC）。無期限・不明なら null。</summary>
    public static DateTime? ExpiresAt(int maxAgeDays, DateTime? changedAtUtc) =>
        maxAgeDays > 0 && changedAtUtc is { } changed ? changed.AddDays(maxAgeDays) : null;
}
