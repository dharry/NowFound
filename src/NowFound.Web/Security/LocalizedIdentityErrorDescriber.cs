using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using NowFound.Application.Users;
using NowFound.Infrastructure.Identity;
using NowFound.Web.Resources;

namespace NowFound.Web.Security;

/// <summary>Identity のエラー（パスワード規則など）を表示言語で返す。文言は MessagesResource の Identity.* キー。</summary>
public class LocalizedIdentityErrorDescriber(IStringLocalizer<MessagesResource> localizer) : AppIdentityErrorDescriber
{
    private IdentityError Error(string code, params object[] args) =>
        new() { Code = code, Description = localizer["Identity." + code, args] };

    public override IdentityError DefaultError() => Error(nameof(DefaultError));

    public override IdentityError ConcurrencyFailure() => Error(nameof(ConcurrencyFailure));

    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch));

    public override IdentityError InvalidToken() => Error(nameof(InvalidToken));

    public override IdentityError RecoveryCodeRedemptionFailed() => Error(nameof(RecoveryCodeRedemptionFailed));

    public override IdentityError LoginAlreadyAssociated() => Error(nameof(LoginAlreadyAssociated));

    public override IdentityError InvalidUserName(string? userName) => Error(nameof(InvalidUserName), userName ?? string.Empty);

    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail), email ?? string.Empty);

    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateUserName), userName);

    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail), email);

    public override IdentityError InvalidRoleName(string? role) => Error(nameof(InvalidRoleName), role ?? string.Empty);

    public override IdentityError DuplicateRoleName(string role) => Error(nameof(DuplicateRoleName), role);

    public override IdentityError UserAlreadyHasPassword() => Error(nameof(UserAlreadyHasPassword));

    public override IdentityError UserLockoutNotEnabled() => Error(nameof(UserLockoutNotEnabled));

    public override IdentityError UserAlreadyInRole(string role) => Error(nameof(UserAlreadyInRole), role);

    public override IdentityError UserNotInRole(string role) => Error(nameof(UserNotInRole), role);

    public override IdentityError PasswordTooShort(int length) => Error(nameof(PasswordTooShort), length);

    public override IdentityError PasswordRequiresNonAlphanumeric() => Error(nameof(PasswordRequiresNonAlphanumeric));

    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit));

    public override IdentityError PasswordRequiresLower() => Error(nameof(PasswordRequiresLower));

    public override IdentityError PasswordRequiresUpper() => Error(nameof(PasswordRequiresUpper));

    public override IdentityError PasswordComplexity() => Error(nameof(PasswordComplexity), AccountPolicy.MinCategories);

    public override IdentityError PasswordReused(int count) => Error(nameof(PasswordReused), count);

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => Error(nameof(PasswordRequiresUniqueChars), uniqueChars);
}
