using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NowFound.Application;
using NowFound.Application.Settings;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Web.Models;
using NowFound.Web.Services;

namespace NowFound.Web.Controllers;

/// <summary>検索対象フォルダの管理（管理者のみ）。</summary>
[Authorize(Roles = AppRoles.Admin)]
public class FoldersController(IAdminSettingsService settings, RequestLogger requestLogger) : Controller
{
    public async Task<IActionResult> Index(ListQueryModel query, CancellationToken ct = default)
    {
        var result = await settings.ListFoldersPageAsync(
            query.Page,
            await settings.GetAdminListPageSizeAsync(ct),
            new FolderListQuery(query.Q, query.SortOrNull, query.Desc),
            ct);
        query.Page = result.Page;
        return View(new ListPage<FolderSummary, ListQueryModel>(result, query));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct) => View("Edit", new FolderEditViewModel
    {
        // 新しく追加するフォルダには、管理者が設定した既定の除外パターンを最初から入れておく。
        ExcludePatterns = string.Join(Environment.NewLine, await settings.GetDefaultExcludePatternsAsync(ct)),
    });

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var folder = await settings.GetFolderAsync(id, ct);
        return folder is null ? NotFound() : View(FolderEditViewModel.FromDto(folder));
    }

    [HttpPost]
    public async Task<IActionResult> Save(FolderEditViewModel model, CancellationToken ct)
    {
        var result = await settings.SaveFolderAsync(model.ToDto(), ct);
        if (result.Succeeded)
        {
            var key = model.Id == 0 ? "Log.Folders.Added" : "Log.Folders.Updated";
            await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage(key, model.Name, model.Path));
            return RedirectToAction(nameof(Index));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        return View("Edit", model);
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var folder = await settings.GetFolderAsync(id, ct);
        await settings.DeleteFolderAsync(id, ct);
        if (folder is not null)
        {
            await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Folders.Deleted", folder.Name, folder.Path));
        }

        return RedirectToAction(nameof(Index));
    }
}
