using Microsoft.AspNetCore.Identity;

namespace NowFound.Infrastructure.Identity;

/// <summary>Identity 標準にないエラー（パスワードの複雑性）を足したエラー記述子。Web 層が表示言語に翻訳する版で置き換える。</summary>
public class AppIdentityErrorDescriber : IdentityErrorDescriber
{
    public const string PasswordComplexityCode = "PasswordComplexity";
    public const string PasswordReusedCode = "PasswordReused";

    public virtual IdentityError PasswordComplexity() => new()
    {
        Code = PasswordComplexityCode,
        Description = "Passwords must contain at least 3 of: uppercase, lowercase, digits and symbols.",
    };

    public virtual IdentityError PasswordReused(int count) => new()
    {
        Code = PasswordReusedCode,
        Description = $"This password was used recently. Choose a password different from your last {count} passwords.",
    };
}
