using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NowFound.Application;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Web.Models;
using NowFound.Web.Services;

namespace NowFound.Web.Controllers;

/// <summary>グループ管理。メンバーと閲覧可能フォルダの割り当てを行う（管理者のみ）。</summary>
[Authorize(Roles = AppRoles.Admin)]
public class GroupsController(IAdminSettingsService settings, IUserAdminService users, RequestLogger requestLogger) : Controller
{
    public async Task<IActionResult> Index(ListQueryModel query, CancellationToken ct = default)
    {
        var result = await settings.ListGroupsPageAsync(
            query.Page,
            await settings.GetAdminListPageSizeAsync(ct),
            new GroupListQuery(query.Q, query.SortOrNull, query.Desc),
            ct);
        query.Page = result.Page;
        return View(new ListPage<GroupSummary, ListQueryModel>(result, query));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct) => View("Edit", await WithOptionsAsync(new GroupEditViewModel(), ct));

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var group = await settings.GetGroupAsync(id, ct);
        if (group is null)
        {
            return NotFound();
        }

        return View(await WithOptionsAsync(
            new GroupEditViewModel
            {
                Id = group.Id,
                Name = group.Name,
                Description = group.Description,
                IsDefault = group.IsDefault,
                UserIds = group.UserIds,
                FolderIds = group.FolderIds,
            },
            ct));
    }

    [HttpPost]
    public async Task<IActionResult> Save(GroupEditViewModel model, CancellationToken ct)
    {
        var result = await settings.SaveGroupAsync(model.ToDto(), ct);
        if (result.Succeeded)
        {
            var key = model.Id == 0 ? "Log.Groups.Added" : "Log.Groups.Updated";
            await requestLogger.LogAsync(
                OperationType.AdminChange,
                new LocalizedMessage(key, model.Name, model.UserIds.Count, model.FolderIds.Count));
            return RedirectToAction(nameof(Index));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        return View("Edit", await WithOptionsAsync(model, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var group = await settings.GetGroupAsync(id, ct);
        var result = await settings.DeleteGroupAsync(id, ct);
        if (!result.Succeeded)
        {
            TempData["FlashError"] = this.TranslateAll(result.Errors);
        }
        else if (group is not null)
        {
            await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Groups.Deleted", group.Name));
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<GroupEditViewModel> WithOptionsAsync(GroupEditViewModel model, CancellationToken ct)
    {
        model.AllUsers = await users.ListAsync(ct);
        model.AllFolders = await settings.ListFoldersAsync(ct);
        return model;
    }
}
