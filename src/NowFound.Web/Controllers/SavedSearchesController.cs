using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NowFound.Application;
using NowFound.Application.Search;
using NowFound.Application.Users;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Web.Models;
using NowFound.Web.Resources;
using NowFound.Web.Services;

namespace NowFound.Web.Controllers;

/// <summary>
/// 保存した検索（ブックマーク）。非公開は本人のみ、公開は全員が実行でき、公開の作成・変更・削除は管理者のみ。
/// 権限の判定はサービス層で行い、ここでは結果の表示と操作ログ（公開の変更のみ）を扱う。
/// </summary>
[Authorize]
public class SavedSearchesController(
    ISavedSearchService service,
    ISearchService searchService,
    IUserAdminService userAdminService,
    RequestLogger requestLogger,
    IStringLocalizer<SavedSearchesResource> localizer) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private bool IsAdmin => User.IsInRole(AppRoles.Admin);

    public async Task<IActionResult> Index(BookmarksQueryModel query, CancellationToken ct = default)
    {
        var pageSize = await userAdminService.GetBookmarkPageSizeAsync(UserId, ct) ?? SystemSetting.DefaultListPageSize;
        var result = await service.ListPageAsync(
            UserId, IsAdmin, query.Page, pageSize,
            new SavedSearchListQuery(query.Q, query.Visibility, query.SortOrNull, query.Desc), ct);
        query.Page = result.Page;
        return View(new ListPage<SavedSearchSummary, BookmarksQueryModel>(result, query));
    }

    /// <summary>保存した条件で検索画面を開く。結果は常に最新で、実行ユーザー自身のアクセス権で絞り込まれる。</summary>
    [HttpGet]
    public async Task<IActionResult> Run(int id, CancellationToken ct)
    {
        var detail = await service.GetAsync(id, UserId, IsAdmin, ct);
        if (detail is null)
        {
            TempData["FlashError"] = localizer["SavedSearches.Flash.NotFound"].Value;
            return RedirectToAction(nameof(Index));
        }

        if (detail.Query is null)
        {
            TempData["FlashError"] = localizer["SavedSearches.Flash.LoadFailed", detail.Summary.Name].Value;
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction("Index", "Home", SearchViewModel.FromQuery(detail.Query, id).ToRouteValues(1));
    }

    [HttpPost]
    public async Task<IActionResult> Create(SaveSearchForm form, CancellationToken ct)
    {
        var result = await service.CreateAsync(
            UserId, IsAdmin, new SavedSearchMeta(form.Name, form.Description, form.IsPublic, form.IsPinned), form.ToSavedQuery(), ct);

        if (!result.Result.Succeeded)
        {
            TempData["FlashError"] = this.TranslateAll(result.Result.Errors);
            return RedirectToAction("Index", "Home", ToViewModel(form, null).ToRouteValues(1));
        }

        if (form.IsPublic)
        {
            await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Search.PublicAdded", form.Name.Trim()));
        }

        TempData["Flash"] = localizer["SavedSearches.Flash.Created", form.Name.Trim()].Value;
        return RedirectToAction("Index", "Home", ToViewModel(form, result.Id).ToRouteValues(1));
    }

    /// <summary>検索条件だけを現在の内容で上書きする。</summary>
    [HttpPost]
    public async Task<IActionResult> Overwrite(int id, SearchQueryInput query, CancellationToken ct)
    {
        var before = await service.GetAsync(id, UserId, IsAdmin, ct);
        var result = await service.OverwriteQueryAsync(id, UserId, IsAdmin, query.ToSavedQuery(), ct);

        if (result.Succeeded)
        {
            if (before?.Summary.IsPublic == true)
            {
                await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Search.PublicOverwritten", before.Summary.Name));
            }

            TempData["Flash"] = localizer["SavedSearches.Flash.Overwritten", before?.Summary.Name ?? string.Empty].Value;
        }
        else
        {
            TempData["FlashError"] = this.TranslateAll(result.Errors);
        }

        var model = new SearchViewModel
        {
            Conditions = query.Conditions,
            Operator = query.Operator,
            FolderId = query.FolderId,
            FolderPath = query.FolderPath,
            Sort = query.Sort,
            Desc = query.Desc,
            Saved = id,
        };
        return RedirectToAction("Index", "Home", model.ToRouteValues(1));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken ct)
    {
        var detail = await service.GetAsync(id, UserId, IsAdmin, ct);
        if (detail is null)
        {
            return NotFound();
        }

        if (!detail.Summary.CanEdit)
        {
            TempData["FlashError"] = localizer["SavedSearches.Flash.NoPermission"].Value;
            return RedirectToAction(nameof(Index));
        }

        var model = ToEditModel(detail.Summary);
        if (detail.Query is { } query)
        {
            model.Conditions = [.. query.Conditions];
            model.Operator = query.Operator;
            model.FolderId = query.FolderId;
            model.FolderPath = query.FolderPath;
            model.Sort = query.Sort;
            model.Desc = query.Descending;
        }

        await PrepareQueryEditorAsync(model, ct);
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Save(SavedSearchEditViewModel model, CancellationToken ct)
    {
        var before = await service.GetAsync(model.Id, UserId, IsAdmin, ct);
        model.FolderPath = model.FolderId is null ? null : model.FolderPath;
        var result = await service.UpdateAsync(
            model.Id, UserId, IsAdmin, new SavedSearchMeta(model.Name, model.Description, model.IsPublic, model.IsPinned), model.ToSavedQuery(), ct);

        if (result.Succeeded)
        {
            if (before?.Summary.IsPublic == true || model.IsPublic)
            {
                await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Search.PublicUpdated", model.Name.Trim()));
            }

            TempData["Flash"] = localizer["SavedSearches.Flash.Updated"].Value;
            return RedirectToAction(nameof(Index));
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, this.Translate(error));
        }

        if (before is not null)
        {
            var current = ToEditModel(before.Summary);
            model.CanPublish = current.CanPublish;
            model.CanMakePrivate = current.CanMakePrivate;
            model.OwnerEmail = current.OwnerEmail;
        }

        await PrepareQueryEditorAsync(model, ct);
        return View("Edit", model);
    }

    /// <summary>編集画面の選択肢を用意し、条件行が 1 つもなければ空の行を 1 つ出す。</summary>
    private async Task PrepareQueryEditorAsync(SavedSearchEditViewModel model, CancellationToken ct)
    {
        model.Folders = await searchService.GetAccessibleFoldersAsync(UserId, IsAdmin, ct);
        if (model.Conditions.Count == 0)
        {
            model.Conditions = [new SearchCondition()];
        }
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var before = await service.GetAsync(id, UserId, IsAdmin, ct);
        var result = await service.DeleteAsync(id, UserId, IsAdmin, ct);

        if (result.Succeeded)
        {
            if (before?.Summary.IsPublic == true)
            {
                await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Search.PublicDeleted", before.Summary.Name));
            }

            TempData["Flash"] = localizer["SavedSearches.Flash.Deleted", before?.Summary.Name ?? string.Empty].Value;
        }
        else
        {
            TempData["FlashError"] = this.TranslateAll(result.Errors);
        }

        return RedirectToAction(nameof(Index));
    }

    private SavedSearchEditViewModel ToEditModel(SavedSearchSummary summary) => new()
    {
        Id = summary.Id,
        Name = summary.Name,
        Description = summary.Description,
        IsPublic = summary.IsPublic,
        IsPinned = summary.IsPinned,
        CanPublish = IsAdmin,
        // 公開のものを非公開に戻せるのは作成者のみ（サービス層でも検証される）。
        CanMakePrivate = !summary.IsPublic || string.Equals(summary.OwnerEmail, User.Identity?.Name, StringComparison.OrdinalIgnoreCase),
        OwnerEmail = summary.OwnerEmail,
    };

    private static SearchViewModel ToViewModel(SearchQueryInput input, int? savedId) => new()
    {
        Conditions = input.Conditions,
        Operator = input.Operator,
        FolderId = input.FolderId,
        FolderPath = input.FolderPath,
        Sort = input.Sort,
        Desc = input.Desc,
        Saved = savedId,
    };
}
