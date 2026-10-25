using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NowFound.Application;
using NowFound.Application.FileKinds;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Web.Models;
using NowFound.Web.Resources;
using NowFound.Web.Services;

namespace NowFound.Web.Controllers;

/// <summary>システム設定（ページサイズ既定値、ファイルの種類とアイコン）。管理者のみ。</summary>
[Authorize(Roles = AppRoles.Admin)]
public class SettingsController(
    IAdminSettingsService settings,
    IFileKindService fileKinds,
    IAccountPolicyService accountPolicy,
    IActiveDirectorySettingsService activeDirectory,
    IDirectoryAuthenticator directoryAuthenticator,
    RequestLogger requestLogger,
    IMessageTranslator translator,
    IStringLocalizer<SettingsResource> localizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var incremental = await settings.GetIncrementalSearchAsync(ct);
        var export = await settings.GetExportAsync(ct);
        return View(new PageSizeViewModel
        {
            PageSize = await settings.GetDefaultPageSizeAsync(ct),
            LogPageSize = await settings.GetLogPageSizeAsync(ct),
            AdminListPageSize = await settings.GetAdminListPageSizeAsync(ct),
            IncrementalEnabled = incremental.Enabled,
            IncrementalMinChars = incremental.MinChars,
            IncrementalDelayMs = incremental.DelayMs,
            ExportAllowAllUsers = export.AllowAllUsers,
            ExportMaxRows = export.MaxRows,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Index(PageSizeViewModel model, CancellationToken ct)
    {
        var result = await settings.SetDefaultPageSizeAsync(model.PageSize ?? 0, ct);
        if (result.Succeeded && model.LogPageSize is { } logPageSize)
        {
            result = await settings.SetLogPageSizeAsync(logPageSize, ct);
        }

        if (result.Succeeded && model.AdminListPageSize is { } adminListPageSize)
        {
            result = await settings.SetAdminListPageSizeAsync(adminListPageSize, ct);
        }

        if (result.Succeeded && model.IncrementalMinChars is { } minChars && model.IncrementalDelayMs is { } delayMs)
        {
            result = await settings.SetIncrementalSearchAsync(
                new IncrementalSearchSettings(model.IncrementalEnabled, minChars, delayMs), ct);
        }

        if (result.Succeeded && model.ExportMaxRows is { } exportMaxRows)
        {
            result = await settings.SetExportAsync(new ExportSettings(model.ExportAllowAllUsers, exportMaxRows), ct);
        }

        if (result.Succeeded)
        {
            await requestLogger.LogAsync(
                OperationType.AdminChange,
                new LocalizedMessage(
                    "Log.Settings.PageSizeChangedWithLists",
                    model.PageSize,
                    await settings.GetLogPageSizeAsync(ct),
                    await settings.GetAdminListPageSizeAsync(ct)));
            if (model.IncrementalMinChars is not null && model.IncrementalDelayMs is not null)
            {
                var incremental = await settings.GetIncrementalSearchAsync(ct);
                await requestLogger.LogAsync(
                    OperationType.AdminChange,
                    new LocalizedMessage(
                        "Log.Settings.IncrementalSearchChanged",
                        incremental.Enabled ? "on" : "off",
                        incremental.MinChars,
                        incremental.DelayMs));
            }

            if (model.ExportMaxRows is not null)
            {
                var export = await settings.GetExportAsync(ct);
                await requestLogger.LogAsync(
                    OperationType.AdminChange,
                    new LocalizedMessage(
                        "Log.Settings.ExportChanged",
                        export.AllowAllUsers ? "all users" : "admins only",
                        export.MaxRows));
            }

            TempData["Message"] = localizer["Settings.Saved"].Value;
            return RedirectToAction(nameof(Index));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        return View(model);
    }

    /// <summary>アカウントポリシー（パスワードの最小文字数・複雑性、ログイン失敗でのロック回数）。</summary>
    [HttpGet]
    public async Task<IActionResult> AccountPolicy(CancellationToken ct)
    {
        var policy = await accountPolicy.GetAsync(ct);
        return View(new AccountPolicyViewModel
        {
            PasswordMinLength = policy.PasswordMinLength,
            RequireComplexity = policy.RequireComplexity,
            MaxFailedAttempts = policy.MaxFailedAttempts,
            PasswordMaxAgeDays = policy.PasswordMaxAgeDays,
            PasswordHistoryCount = policy.PasswordHistoryCount,
        });
    }

    [HttpPost]
    public async Task<IActionResult> AccountPolicy(AccountPolicyViewModel model, CancellationToken ct)
    {
        var policy = new Application.Users.AccountPolicy(
            model.PasswordMinLength ?? 0,
            model.RequireComplexity,
            model.MaxFailedAttempts ?? -1,
            model.PasswordMaxAgeDays ?? -1,
            model.PasswordHistoryCount ?? -1);
        var result = await accountPolicy.SetAsync(policy, ct);
        if (result.Succeeded)
        {
            await requestLogger.LogAsync(
                OperationType.AdminChange,
                new LocalizedMessage(
                    "Log.Settings.AccountPolicyChanged",
                    policy.PasswordMinLength,
                    policy.RequireComplexity ? "on" : "off",
                    policy.MaxFailedAttempts,
                    policy.PasswordMaxAgeDays,
                    policy.PasswordHistoryCount));
            TempData["Message"] = localizer["Settings.Saved"].Value;
            return RedirectToAction(nameof(AccountPolicy));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        return View(model);
    }

    /// <summary>ActiveDirectory 認証の設定（サーバー・接続方式・ドメイン）。</summary>
    [HttpGet]
    public async Task<IActionResult> ActiveDirectory(CancellationToken ct)
    {
        var current = await activeDirectory.GetAsync(ct);
        return View(new ActiveDirectoryViewModel
        {
            Enabled = current.Enabled,
            Server = current.Server,
            Port = current.Port,
            Security = current.Security,
            Domain = current.Domain,
            BindUser = current.BindUser,
            SearchBase = current.SearchBase,
            HasBindPassword = await activeDirectory.HasBindPasswordAsync(ct),
        });
    }

    [HttpPost]
    public async Task<IActionResult> ActiveDirectory(ActiveDirectoryViewModel model, CancellationToken ct)
    {
        var next = ToSettings(model, model.Enabled);
        var result = await activeDirectory.SetAsync(next, model.BindPassword, ct);
        if (result.Succeeded)
        {
            await requestLogger.LogAsync(
                OperationType.AdminChange,
                new LocalizedMessage(
                    "Log.Settings.ActiveDirectoryChanged",
                    next.Enabled ? "on" : "off",
                    next.Server,
                    next.Port,
                    next.Security.ToString(),
                    next.Domain));
            TempData["Message"] = localizer["Settings.Saved"].Value;
            return RedirectToAction(nameof(ActiveDirectory));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        model.BindPassword = null;
        model.HasBindPassword = await activeDirectory.HasBindPasswordAsync(ct);
        return View(model);
    }

    /// <summary>
    /// 入力中の（保存前の）設定で、接続（と TLS、確認用アカウントのバインド）を確認する。
    /// パスワードが空なら、保存済みの確認用アカウントのパスワードを使う。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> ActiveDirectoryTest(ActiveDirectoryViewModel model, CancellationToken ct)
    {
        var target = ToSettings(model, enabled: false);
        var invalid = ActiveDirectoryRules.Validate(target);
        if (!invalid.Succeeded)
        {
            return Json(new { success = false, message = this.TranslateAll(invalid.Errors) });
        }

        var bindPassword = string.IsNullOrEmpty(model.BindPassword)
            ? await activeDirectory.GetBindPasswordAsync(ct)
            : model.BindPassword;
        var result = await directoryAuthenticator.CheckConnectionAsync(target, bindPassword, ct);
        var message = result.Status switch
        {
            DirectoryCheckStatus.Success when result.BindTested => this.Translate("Ad.Test.SuccessBind"),
            DirectoryCheckStatus.Success when target.BindUser.Length > 0 => this.Translate("Ad.Test.SuccessBindSkipped"),
            DirectoryCheckStatus.Success => this.Translate("Ad.Test.Success"),
            DirectoryCheckStatus.NotConfigured => this.Translate("Ad.Test.NoServer"),
            DirectoryCheckStatus.BindRejected => this.Translate("Ad.Test.BindRejected", result.Detail),
            _ => this.Translate("Ad.Test.ConnectFailed", result.Detail),
        };
        return Json(new { success = result.Succeeded, message });
    }

    private static ActiveDirectorySettings ToSettings(ActiveDirectoryViewModel model, bool enabled) => new(
        enabled,
        model.Server?.Trim() ?? string.Empty,
        model.Port ?? 0,
        model.Security,
        model.Domain?.Trim() ?? string.Empty,
        model.BindUser?.Trim() ?? string.Empty,
        model.SearchBase?.Trim() ?? string.Empty);

    /// <summary>新しく追加する検索対象フォルダに、最初から入れる除外パターンの編集。</summary>
    [HttpGet]
    public async Task<IActionResult> ExcludeDefaults(CancellationToken ct) => View(await LoadExcludeDefaultsAsync(ct));

    [HttpPost]
    public async Task<IActionResult> ExcludeDefaults(ExcludeDefaultsViewModel model, CancellationToken ct)
    {
        var patterns = ExcludePatternRules.Split(model.Patterns);
        var result = await settings.SetDefaultExcludePatternsAsync(patterns, ct);
        if (result.Succeeded)
        {
            await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Settings.ExcludeDefaultsUpdated", patterns.Count));
            TempData["Message"] = localizer["Settings.Exclude.Saved"].Value;
            return RedirectToAction(nameof(ExcludeDefaults));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        model.IsCustomized = await settings.IsDefaultExcludePatternsCustomizedAsync(ct);
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> ResetExcludeDefaults(CancellationToken ct)
    {
        await settings.ResetDefaultExcludePatternsAsync(ct);
        await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Settings.ExcludeDefaultsReset"));
        TempData["Message"] = localizer["Settings.Exclude.ResetDone"].Value;
        return RedirectToAction(nameof(ExcludeDefaults));
    }

    /// <summary>ファイルの種類（検索結果の名前欄に出すアイコン・名前・拡張子）の編集。</summary>
    [HttpGet]
    public async Task<IActionResult> FileKinds(CancellationToken ct) => View(await LoadKindsAsync(ct));

    [HttpPost]
    public async Task<IActionResult> FileKinds(FileKindsEditViewModel model, CancellationToken ct)
    {
        // 組み込みの既定の名前を、表示言語に翻訳された名前のまま保存しても「改名」扱いにならないよう、既定の名前に戻す。
        var builtIn = DefaultFileKinds.Definitions.ToDictionary(d => d.Key);
        var edits = (model.Rows ?? []).Select(r => new FileKindEdit
        {
            Key = r.Key,
            Label = builtIn.TryGetValue(r.Key ?? string.Empty, out var d)
                && r.Label == translator.FileKindLabel(new FileKind(d.Key, d.Label, d.Icon, d.LabelKey))
                    ? d.Label
                    : r.Label,
            Icon = r.Icon,
            Extensions = r.Extensions,
        }).ToList();

        var result = await fileKinds.SaveAsync(edits, ct);
        if (result.Succeeded)
        {
            await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Settings.FileKindsUpdated", edits.Count));
            TempData["Message"] = localizer["Settings.Kinds.Saved"].Value;
            return RedirectToAction(nameof(FileKinds));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        // 入力内容は失わずに、そのまま編集を続けられるようにする。
        model.Rows ??= [];
        model.IsCustomized = await fileKinds.IsCustomizedAsync(ct);
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> ResetFileKinds(CancellationToken ct)
    {
        await fileKinds.ResetAsync(ct);
        await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Settings.FileKindsReset"));
        TempData["Message"] = localizer["Settings.Kinds.ResetDone"].Value;
        return RedirectToAction(nameof(FileKinds));
    }

    private async Task<ExcludeDefaultsViewModel> LoadExcludeDefaultsAsync(CancellationToken ct) => new()
    {
        Patterns = string.Join(Environment.NewLine, await settings.GetDefaultExcludePatternsAsync(ct)),
        IsCustomized = await settings.IsDefaultExcludePatternsCustomizedAsync(ct),
    };

    private async Task<FileKindsEditViewModel> LoadKindsAsync(CancellationToken ct) => new()
    {
        IsCustomized = await fileKinds.IsCustomizedAsync(ct),
        Rows =
        [
            .. (await fileKinds.GetDefinitionsAsync(ct)).Select(d => new FileKindRowViewModel
            {
                Key = d.Key,
                // 組み込みの既定の名前は表示言語に合わせる（管理者が付けた名前はそのまま）。
                Label = translator.FileKindLabel(new FileKind(d.Key, d.Label, d.Icon, d.LabelKey)),
                Icon = d.Icon,
                Extensions = string.Join(", ", d.Extensions),
            }),
        ],
    };
}
