using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NowFound.Domain;
using NowFound.Infrastructure.Identity;

namespace NowFound.Web.Security;

/// <summary>パスワード変更要求フラグをクレームとして Cookie に載せる。</summary>
public class AppUserClaimsPrincipalFactory(
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(userManager, roleManager, options)
{
    public const string MustChangePasswordClaim = "must_change_password";
    public const string ThemeClaim = "theme";
    public const string LanguageClaim = "language";
    public const string AuthSourceClaim = "auth_source";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.MustChangePassword)
        {
            identity.AddClaim(new Claim(MustChangePasswordClaim, "true"));
        }

        identity.AddClaim(new Claim(AuthSourceClaim, AuthSources.Normalize(user.AuthSource)));
        identity.AddClaim(new Claim(ThemeClaim, AppThemes.Normalize(user.Theme)));
        if (AppLanguages.IsValid(user.Language))
        {
            identity.AddClaim(new Claim(LanguageClaim, user.Language!));
        }

        return identity;
    }
}
