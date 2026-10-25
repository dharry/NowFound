using NowFound.Application.Users;

namespace NowFound.UnitTests;

public class AccountPolicyTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 1)]
    [InlineData("abcDEF", 2)]
    [InlineData("abcDEF123", 3)]
    [InlineData("abcDEF123!", 4)]
    [InlineData("password123!", 3)]
    [InlineData("日本語パスワード", 1)]
    [InlineData("abc 123", 3)]
    public void CountCategories_CountsUpperLowerDigitAndSymbol(string password, int expected) =>
        Assert.Equal(expected, PasswordRules.CountCategories(password));

    [Fact]
    public void Validate_TooShort_ReportsTooShort()
    {
        var policy = new AccountPolicy(8, false, 5);

        Assert.Equal([PasswordViolation.TooShort], PasswordRules.Validate("abc1234", policy));
        Assert.Empty(PasswordRules.Validate("abc12345", policy));
    }

    [Fact]
    public void Validate_WithComplexity_RequiresThreeCategories()
    {
        var policy = new AccountPolicy(5, true, 5);

        Assert.Equal([PasswordViolation.Complexity], PasswordRules.Validate("abcdefgh", policy));
        Assert.Equal([PasswordViolation.Complexity], PasswordRules.Validate("abcdef12", policy));
        Assert.Empty(PasswordRules.Validate("abcDef12", policy));
        Assert.Empty(PasswordRules.Validate("abc123!!", policy));
    }

    [Fact]
    public void Validate_WithoutComplexity_AllowsSingleCategory()
    {
        var policy = new AccountPolicy(5, false, 5);

        Assert.Empty(PasswordRules.Validate("abcdefgh", policy));
    }

    [Fact]
    public void Validate_BothViolations_AreReported()
    {
        var policy = new AccountPolicy(10, true, 5);

        Assert.Equal([PasswordViolation.TooShort, PasswordViolation.Complexity], PasswordRules.Validate("abc", policy));
    }

    [Theory]
    [InlineData(5, 0, true)]
    [InlineData(127, 10, true)]
    [InlineData(4, 5, false)]
    [InlineData(128, 5, false)]
    [InlineData(8, -1, false)]
    [InlineData(8, 11, false)]
    public void IsValid_ChecksTheDocumentedRanges(int minLength, int maxFailed, bool expected) =>
        Assert.Equal(expected, new AccountPolicy(minLength, true, maxFailed).IsValid);

    [Fact]
    public void Default_IsValid_AndAcceptsTheInitialAdminPassword()
    {
        Assert.True(AccountPolicy.Default.IsValid);
        Assert.Empty(PasswordRules.Validate("password123!", AccountPolicy.Default));
    }

    private static readonly DateOnly Today = new(2026, 10, 9);

    [Fact]
    public void Status_Disabled_WinsOverExpiry() =>
        Assert.Equal(AccountStatus.Disabled, AccountStatusRules.Evaluate(false, Today.AddDays(-1), Today));

    [Fact]
    public void Status_NoExpiry_IsActive() =>
        Assert.Equal(AccountStatus.Active, AccountStatusRules.Evaluate(true, null, Today));

    [Fact]
    public void Status_ExpiresToday_IsStillActive() =>
        Assert.Equal(AccountStatus.Active, AccountStatusRules.Evaluate(true, Today, Today));

    [Fact]
    public void Status_ExpiredYesterday_IsExpired() =>
        Assert.Equal(AccountStatus.Expired, AccountStatusRules.Evaluate(true, Today.AddDays(-1), Today));
}
