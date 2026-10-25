using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NowFound.Application;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Web.Models;
using NowFound.Web.Security;
using NowFound.Web.Services;

namespace NowFound.Web.Controllers;

[Authorize(Roles = AppRoles.Admin)]
public class UsersController(
    IUserAdminService userAdminService,
    IAdminSettingsService settings,
    IActiveDirectorySettingsService directorySettings,
    IDirectoryAuthenticator directoryAuthenticator,
    RequestLogger requestLogger) : Controller
{
    public async Task<IActionResult> Index(UsersQueryModel query, CancellationToken ct = default)
    {
        var result = await userAdminService.ListPageAsync(
            query.Page,
            await settings.GetAdminListPageSizeAsync(ct),
            new UserListQuery(query.Q, query.Role, query.SortOrNull, query.Desc),
            ct);
        query.Page = result.Page;
        return View(new ListPage<UserSummary, UsersQueryModel>(result, query));
    }

    [HttpGet]
    public IActionResult Create() => View(new CreateUserViewModel());

    /// <summary>ユーザー追加画面で入力した sAMAccountName が、ActiveDirectory に存在するかを確認する。</summary>
    [HttpPost]
    public async Task<IActionResult> CheckDirectoryUser(string? email, CancellationToken ct)
    {
        var userName = ActiveDirectoryRules.NormalizeUserName(email);
        if (userName is null)
        {
            return Json(new { success = false, message = this.Translate("Users.Check.InvalidName") });
        }

        var result = await directoryAuthenticator.FindUserAsync(
            await directorySettings.GetAsync(ct), await directorySettings.GetBindPasswordAsync(ct), userName, ct);
        var message = result.Status switch
        {
            DirectoryLookupStatus.Found when result.AccountDisabled => this.Translate("Users.Check.FoundDisabled", userName, result.DisplayName ?? "-"),
            DirectoryLookupStatus.Found => this.Translate("Users.Check.Found", userName, result.DisplayName ?? "-"),
            DirectoryLookupStatus.NotFound => this.Translate("Users.Check.NotFound", userName),
            DirectoryLookupStatus.NotConfigured => this.Translate("Users.Check.NotConfigured"),
            DirectoryLookupStatus.BindRejected => this.Translate("Users.Check.BindRejected", result.Detail),
            DirectoryLookupStatus.SearchFailed => this.Translate("Users.Check.SearchFailed", result.Detail),
            _ => this.Translate("Users.Check.ConnectFailed", result.Detail),
        };
        return Json(new { success = result.Status == DirectoryLookupStatus.Found, message });
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserViewModel model)
    {
        var result = await userAdminService.CreateAsync(
            new CreateUserRequest(
                model.Email, model.Password ?? string.Empty, model.IsAdmin, model.RequirePasswordChange, model.IsEnabled, model.ExpiresOn,
                AuthSources.Normalize(model.AuthSource), model.MailAddress, model.Description));
        if (result.Succeeded)
        {
            var key = model.IsAdmin ? "Log.Users.AddedAdmin" : "Log.Users.AddedUser";
            await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage(key, model.Email));
            return RedirectToAction(nameof(Index));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id, CancellationToken ct)
    {
        var user = await userAdminService.GetAsync(id, ct);
        if (user is null)
        {
            return NotFound();
        }

        var model = ToViewModel(user);
        model.IsSelf = user.Id == User.FindFirstValue(ClaimTypes.NameIdentifier);
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(UserEditViewModel model, CancellationToken ct)
    {
        // メールアドレスとロック状態は画面に表示するだけなので、フォームの値ではなく保存済みの値を使う。
        var current = await userAdminService.GetAsync(model.Id, ct);
        if (current is null)
        {
            return NotFound();
        }

        model.Email = current.Email;
        // 標準ユーザーの連絡先はアカウントと同じで、フォームでは変更できない。
        if (current.AuthSource != AuthSources.ActiveDirectory)
        {
            model.MailAddress = current.MailAddress;
        }

        model.IsLockedOut = current.IsLockedOut;
        model.AuthSource = current.AuthSource;
        model.IsSelf = model.Id == User.FindFirstValue(ClaimTypes.NameIdentifier);

        var actingUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await userAdminService.UpdateAsync(
            new UserEdit
            {
                Id = model.Id,
                IsAdmin = model.IsAdmin,
                IsEnabled = model.IsEnabled,
                ExpiresOn = model.ExpiresOn,
                MailAddress = model.MailAddress,
                Description = model.Description,
            },
            actingUserId,
            AccountClock.Today,
            ct);
        if (result.Succeeded)
        {
            await requestLogger.LogAsync(
                OperationType.AdminChange,
                new LocalizedMessage(
                    "Log.Users.Updated",
                    model.Email,
                    model.IsAdmin ? "Admin" : "User",
                    model.IsEnabled ? "enabled" : "disabled",
                    model.ExpiresOn?.ToString("yyyy-MM-dd") ?? "-"));
            return RedirectToAction(nameof(Index));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Unlock(string id, string? email, CancellationToken ct)
    {
        var result = await userAdminService.UnlockAsync(id, ct);
        if (result.Succeeded)
        {
            await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Users.Unlocked", email ?? id));
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>管理者が標準ユーザーのパスワードを再設定する（パスワードを忘れたとき）。</summary>
    [HttpPost]
    public async Task<IActionResult> ResetPassword(string id, string? newPassword, bool requireChange, CancellationToken ct)
    {
        var current = await userAdminService.GetAsync(id, ct);
        if (current is null)
        {
            return NotFound();
        }

        var result = await userAdminService.ResetPasswordAsync(
            id, newPassword ?? string.Empty, requireChange, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
        if (result.Succeeded)
        {
            await requestLogger.LogAsync(
                OperationType.AdminChange,
                new LocalizedMessage("Log.Users.PasswordReset", current.Email, requireChange ? "must change" : "no change required"));
            TempData["Message"] = this.Translate("Notice.UserPasswordReset", current.Email);
        }
        else
        {
            TempData["Error"] = this.TranslateAll(result.Errors);
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var current = await userAdminService.GetAsync(id, ct);
        if (current is null)
        {
            return NotFound();
        }

        var result = await userAdminService.DeleteAsync(id, User.FindFirstValue(ClaimTypes.NameIdentifier)!, AccountClock.Today, ct);
        if (result.Succeeded)
        {
            await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Users.Deleted", current.Email));
            TempData["Message"] = this.Translate("Notice.UserDeleted", current.Email);
            return RedirectToAction(nameof(Index));
        }

        // 削除できない理由（自分自身・最後の管理者）は、編集画面に戻して表示する。
        TempData["Error"] = this.TranslateAll(result.Errors);
        return RedirectToAction(nameof(Edit), new { id });
    }

    private static UserEditViewModel ToViewModel(UserEdit user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        MailAddress = user.MailAddress,
        Description = user.Description,
        IsAdmin = user.IsAdmin,
        IsEnabled = user.IsEnabled,
        ExpiresOn = user.ExpiresOn,
        IsLockedOut = user.IsLockedOut,
        AuthSource = user.AuthSource,
    };
}
