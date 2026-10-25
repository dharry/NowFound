using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using NowFound.Application;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Infrastructure.Identity;
using NowFound.Domain.Entities;
using NowFound.Domain;
using NowFound.Web.Models;
using NowFound.Web.Resources;
using NowFound.Web.Security;
using NowFound.Web.Services;

namespace NowFound.Web.Controllers;

public class AccountController(
    SignInManager<AppUser> signInManager,
    UserManager<AppUser> userManager,
    IUserAdminService userAdminService,
    IAdminSettingsService settings,
    IAccountPolicyStore policyStore,
    IActiveDirectorySettingsService directorySettings,
    IDirectoryAuthenticator directoryAuthenticator,
    RequestLogger requestLogger,
    IStringLocalizer<AccountResource> localizer) : Controller
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl = null, bool blocked = false, CancellationToken ct = default)
    {
        if (blocked)
        {
            // 無効化・期限切れでセッションが切れたとき。
            ModelState.AddModelError(string.Empty, localizer["Login.SessionBlocked"]);
        }

        var directoryConfigured = (await directorySettings.GetAsync(ct)).IsConfigured;
        return View(new LoginViewModel
        {
            ReturnUrl = returnUrl,
            DirectoryEnabled = directoryConfigured,
            AuthSource = AuthSourceHelper.Resolve(HttpContext, directoryConfigured),
        });
    }

    [AllowAnonymous]
    [HttpPost]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        var directory = await directorySettings.GetAsync(ct);
        model.DirectoryEnabled = directory.IsConfigured;
        model.Password ??= string.Empty;

        if (directory.IsConfigured && AuthSources.IsValid(model.AuthSource))
        {
            AuthSourceHelper.Remember(HttpContext, model.AuthSource);
        }

        if (model.AuthSource == AuthSources.ActiveDirectory)
        {
            // タブが表示されないとき（AD 認証が無効・未設定）に AD として送られても、ログインはさせない。
            return directory.IsConfigured
                ? await LoginDirectoryAsync(model, directory, ct)
                : await LoginFailedAsync(model, model.UserName);
        }

        model.AuthSource = AuthSources.Local;
        return await LoginLocalAsync(model);
    }

    private async Task<IActionResult> LoginLocalAsync(LoginViewModel model)
    {
        var user = await userManager.FindByEmailAsync(model.Email);

        // ActiveDirectory のユーザーは Email 欄に sAMAccountName を持つ。標準のログインでは使えない。
        if (user is { IsDirectoryUser: false })
        {
            var status = AccountStatusRules.Evaluate(user.IsEnabled, user.ExpiresOn, AccountClock.Today);
            if (status != AccountStatus.Active)
            {
                // パスワードが合っているときだけ理由を伝える（第三者にアカウントの存在・状態を知らせない）。
                if (await userManager.CheckPasswordAsync(user, model.Password))
                {
                    var expired = status == AccountStatus.Expired;
                    await requestLogger.LogAsync(
                        OperationType.LoginFailed,
                        new LocalizedMessage(expired ? "Log.Account.Expired" : "Log.Account.Disabled"),
                        model.Email);
                    ModelState.AddModelError(string.Empty, localizer[expired ? "Login.Expired" : "Login.Disabled"]);
                    return View(model);
                }
            }
            else
            {
                var result = await signInManager.PasswordSignInAsync(
                    user, model.Password, model.RememberMe, lockoutOnFailure: policyStore.Current.MaxFailedAttempts > 0);
                if (result.Succeeded)
                {
                    await requestLogger.LogAsync(OperationType.Login, email: user.Email, userId: user.Id);
                    ThemeHelper.Remember(HttpContext, user.Theme);
                    if (AppLanguages.IsValid(user.Language))
                    {
                        LanguageHelper.Remember(HttpContext, user.Language);
                    }

                    if (user.MustChangePassword)
                    {
                        return RedirectToAction(nameof(ChangePassword));
                    }

                    // パスワードの有効期間が過ぎていたら、変更するまで他の画面へは進めない（AccountStatusMiddleware）。
                    return PasswordAgeRules.IsExpired(policyStore.Current.PasswordMaxAgeDays, user.PasswordChangedAt, DateTime.UtcNow)
                        ? RedirectToAction(nameof(ChangePassword), new { expired = "true" })
                        : LocalRedirectOrHome(model.ReturnUrl);
                }

                if (result.IsLockedOut)
                {
                    await requestLogger.LogAsync(OperationType.LoginFailed, new LocalizedMessage("Log.Account.LockedOut"), model.Email);
                    ModelState.AddModelError(string.Empty, localizer["Login.Locked"]);
                    return View(model);
                }
            }
        }

        return await LoginFailedAsync(model, model.Email);
    }

    /// <summary>
    /// ActiveDirectory のログイン。登録済みの AD ユーザーだけが対象で、パスワードは AD に検証させる。
    /// ロック・有効/無効・有効期限は標準のユーザーと同じ仕組みで扱う。
    /// </summary>
    private async Task<IActionResult> LoginDirectoryAsync(LoginViewModel model, ActiveDirectorySettings directory, CancellationToken ct)
    {
        var userName = ActiveDirectoryRules.NormalizeUserName(model.UserName);
        var user = userName is null
            ? null
            : await userManager.FindByNameAsync(AuthSources.ActiveDirectoryUserNamePrefix + userName);
        if (userName is null || user is not { IsDirectoryUser: true })
        {
            return await LoginFailedAsync(model, model.UserName);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            return await LoginLockedAsync(model, user.Email);
        }

        var auth = await directoryAuthenticator.AuthenticateAsync(directory, userName, model.Password, ct);
        if (auth == DirectoryAuthStatus.Unavailable)
        {
            await requestLogger.LogAsync(OperationType.LoginFailed, new LocalizedMessage("Log.Account.DirectoryUnavailable"), user.Email);
            ModelState.AddModelError(string.Empty, localizer["Login.DirectoryUnavailable"]);
            return View(model);
        }

        if (auth == DirectoryAuthStatus.InvalidCredentials)
        {
            if (policyStore.Current.MaxFailedAttempts > 0)
            {
                await userManager.AccessFailedAsync(user);
                if (await userManager.IsLockedOutAsync(user))
                {
                    return await LoginLockedAsync(model, user.Email);
                }
            }

            return await LoginFailedAsync(model, user.Email);
        }

        // ここから先は、パスワードが正しい本人。無効・期限切れの理由は本人にだけ伝える。
        var status = AccountStatusRules.Evaluate(user.IsEnabled, user.ExpiresOn, AccountClock.Today);
        if (status != AccountStatus.Active)
        {
            var expired = status == AccountStatus.Expired;
            await requestLogger.LogAsync(
                OperationType.LoginFailed,
                new LocalizedMessage(expired ? "Log.Account.Expired" : "Log.Account.Disabled"),
                user.Email);
            ModelState.AddModelError(string.Empty, localizer[expired ? "Login.Expired" : "Login.Disabled"]);
            return View(model);
        }

        await userManager.ResetAccessFailedCountAsync(user);
        await signInManager.SignInAsync(user, model.RememberMe);
        await requestLogger.LogAsync(OperationType.Login, email: user.Email, userId: user.Id);
        ThemeHelper.Remember(HttpContext, user.Theme);
        if (AppLanguages.IsValid(user.Language))
        {
            LanguageHelper.Remember(HttpContext, user.Language);
        }

        return LocalRedirectOrHome(model.ReturnUrl);
    }

    private async Task<IActionResult> LoginLockedAsync(LoginViewModel model, string? email)
    {
        await requestLogger.LogAsync(OperationType.LoginFailed, new LocalizedMessage("Log.Account.LockedOut"), email);
        ModelState.AddModelError(string.Empty, localizer["Login.Locked"]);
        return View(model);
    }

    private async Task<IActionResult> LoginFailedAsync(LoginViewModel model, string? loginName)
    {
        await requestLogger.LogAsync(OperationType.LoginFailed, new LocalizedMessage("Log.Account.AuthFailed"), loginName);
        ModelState.AddModelError(
            string.Empty, localizer[model.AuthSource == AuthSources.ActiveDirectory ? "Login.InvalidDirectory" : "Login.Invalid"]);
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await requestLogger.LogAsync(OperationType.Logout);
        await signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    /// <summary>
    /// パスワード変更の専用画面。初回ログインや有効期間切れで変更が強制されているときだけ表示する
    /// （他の画面へ進めないため）。通常の変更は「ユーザー設定」の中で行う。
    /// </summary>
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> ChangePassword(bool expired = false)
    {
        var user = (await userManager.FindByIdAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!))!;
        if (!IsPasswordChangeForced(user))
        {
            return RedirectToAction(nameof(Preferences));
        }

        ViewData["Expired"] = expired;
        return View(new ChangePasswordViewModel());
    }

    /// <summary>パスワードを変更する。ユーザー設定の画面と、強制変更の専用画面の両方から受け付ける。</summary>
    [Authorize]
    [HttpPost]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var forced = IsPasswordChangeForced((await userManager.FindByIdAsync(userId))!);

        var errors = new List<string>();
        if (model.NewPassword != model.ConfirmPassword)
        {
            errors.Add(localizer["ChangePassword.Mismatch"]);
        }
        else
        {
            var result = await userAdminService.ChangePasswordAsync(userId, model.CurrentPassword, model.NewPassword);
            errors.AddRange(result.Errors.Select(error => this.Translate(error)));
        }

        if (errors.Count > 0)
        {
            if (forced)
            {
                errors.ForEach(error => ModelState.AddModelError(string.Empty, error));
                return View(model);
            }

            // 通常の変更は、ユーザー設定の画面のパスワード欄にエラーを表示する。
            ViewData["PasswordErrors"] = errors;
            return View(nameof(Preferences), await LoadPreferencesAsync(ct));
        }

        await requestLogger.LogAsync(OperationType.PasswordChange);

        // クレーム（must_change_password）を更新するため再サインインする。
        var user = (await userManager.FindByIdAsync(userId))!;
        await signInManager.RefreshSignInAsync(user);
        if (forced)
        {
            return RedirectToAction("Index", "Home");
        }

        TempData["Message"] = localizer["Preferences.PasswordChanged"].Value;
        return Redirect(Url.Action(nameof(Preferences)) + "#password");
    }

    /// <summary>初回ログイン（管理者が要求）または有効期間切れで、パスワードの変更が強制されているか。</summary>
    private bool IsPasswordChangeForced(AppUser user) =>
        user.MustChangePassword
        || PasswordAgeRules.IsExpired(policyStore.Current.PasswordMaxAgeDays, user.PasswordChangedAt, DateTime.UtcNow);

    /// <summary>ユーザー個別のページサイズ設定。</summary>
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Preferences(CancellationToken ct) => View(await LoadPreferencesAsync(ct));

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> Preferences(PreferencesViewModel model, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await userAdminService.SetPageSizeAsync(userId, model.PageSize, ct);
        if (result.Succeeded)
        {
            result = await userAdminService.SetBookmarkPageSizeAsync(userId, model.BookmarkPageSize, ct);
        }

        if (result.Succeeded)
        {
            result = await userAdminService.SetThemeAsync(userId, model.Theme, ct);
        }

        var languageChanged = result.Succeeded && AppLanguages.IsValid(model.Language);
        if (languageChanged)
        {
            result = await userAdminService.SetLanguageAsync(userId, model.Language!, ct);
        }

        if (result.Succeeded)
        {
            // テーマはクレームに載せているため、再サインインして反映する。
            await signInManager.RefreshSignInAsync((await userManager.FindByIdAsync(userId))!);
            ThemeHelper.Remember(HttpContext, model.Theme);
            if (languageChanged)
            {
                // ログイン前の画面（ログイン画面など）でも選んだ言語で表示する。
                LanguageHelper.Remember(HttpContext, model.Language);

                // 保存の通知は、次の画面と同じ（新しい）言語で作る。
                CultureInfo.CurrentUICulture = new CultureInfo(model.Language!);
            }

            TempData["Message"] = localizer["Preferences.Saved"].Value;
            return RedirectToAction(nameof(Preferences));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        model.DefaultPageSize = await settings.GetDefaultPageSizeAsync(ct);
        model.Language = AppLanguages.Normalize(model.Language);
        return View(model);
    }

    [AllowAnonymous]
    public async Task<IActionResult> AccessDenied(string? returnUrl = null)
    {
        await requestLogger.LogAsync(OperationType.AccessDenied, string.IsNullOrEmpty(returnUrl) ? null : new LocalizedMessage("Log.Account.AccessDenied", returnUrl));
        return View();
    }

    private async Task<PreferencesViewModel> LoadPreferencesAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        return new PreferencesViewModel
        {
            PageSize = await userAdminService.GetPageSizeAsync(userId, ct),
            BookmarkPageSize = await userAdminService.GetBookmarkPageSizeAsync(userId, ct),
            DefaultPageSize = await settings.GetDefaultPageSizeAsync(ct),
            Theme = AppThemes.Normalize(await userAdminService.GetThemeAsync(userId, ct)),
            Language = AppLanguages.Normalize(await userAdminService.GetLanguageAsync(userId, ct)
                ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName),
        };
    }

    private IActionResult LocalRedirectOrHome(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction("Index", "Home");
}
