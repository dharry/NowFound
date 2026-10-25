using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NowFound.Application;
using NowFound.Application.FileKinds;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Web.Models;
using NowFound.Web.Resources;
using NowFound.Web.Services;

namespace NowFound.Web.Controllers;

[Authorize]
public class HomeController(
    ISearchService searchService,
    ISavedSearchService savedSearchService,
    IFileKindService fileKindService,
    IAdminSettingsService adminSettings,
    ExportLimiter exportLimiter,
    RequestLogger requestLogger,
    IStringLocalizer<SearchResource> localizer) : Controller
{
    /// <summary>インクリメンタルサーチ（入力に合わせた自動検索）のリクエストに付けるヘッダー。</summary>
    public const string IncrementalHeader = "X-Incremental-Search";

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private bool IsAdmin => User.IsInRole(AppRoles.Admin);

    public async Task<IActionResult> Index(SearchViewModel model, CancellationToken ct)
    {
        // 使えないサイズ（負の値・大きすぎる値）は無視するが、黙って捨てずに画面で知らせる。
        if (model.Conditions.Any(c => c.HasInvalidSize))
        {
            model.Warnings.Add(localizer["Search.Warning.InvalidSize"].Value);
        }

        var nonEmptyConditions = model.Conditions.Where(c => !c.IsEmpty).ToList();
        var hasValidationError = !ValidateQuery(model, nonEmptyConditions);

        model.Conditions = nonEmptyConditions;
        if (model.Conditions.Count == 0)
        {
            model.Conditions.Add(new SearchCondition());
        }

        var isIncremental = Request.Headers.ContainsKey(IncrementalHeader);

        // 管理者は常にエクスポートできる。一般ユーザーは管理者の設定で許可されている場合のみ。
        model.CanExport = IsAdmin || (await adminSettings.GetExportAsync(ct)).AllowAllUsers;

        // インクリメンタルサーチ時はサイドバーのフォルダ一覧や保存検索一覧、設定等の再取得をスキップし負荷を削減する
        if (isIncremental)
        {
            model.FileKinds = await fileKindService.GetCatalogAsync(ct);
            if (model.Saved is { } savedId)
            {
                var savedDetail = await savedSearchService.GetAsync(savedId, UserId, IsAdmin, ct);
                model.CurrentSaved = savedDetail?.Summary;
            }
        }
        else
        {
            model.Folders = await searchService.GetAccessibleFoldersAsync(UserId, IsAdmin, ct);
            model.SavedSearches = await savedSearchService.ListAsync(UserId, IsAdmin, ct);
            model.FileKinds = await fileKindService.GetCatalogAsync(ct);
            model.Incremental = await adminSettings.GetIncrementalSearchAsync(ct);
            model.CurrentSaved = model.Saved is { } savedId ? model.SavedSearches.FirstOrDefault(s => s.Id == savedId) : null;
            model.FolderUnavailable = model.FolderId is { } folderId && model.Folders.All(f => f.Id != folderId);
        }

        if (hasValidationError)
        {
            var pageSize = await searchService.ResolvePageSizeAsync(UserId, ct);
            model.Result = new SearchPage([], model.Page, pageSize, 0);
            return isIncremental ? PartialView("_SearchResultsPartial", model) : View(model);
        }

        // 条件指定またはフォルダ絞り込みを伴う検索の 1 ページ目のみ記録する（ページ送りでの重複を避ける）。
        // インクリメンタルサーチの途中経過は、入力のたびに記録が溢れるため記録しない（Enter / 検索ボタンでの実行は記録する）。
        if (!isIncremental && model.Page <= 1 && (model.FolderId is not null || model.Conditions.Any(c => !c.IsEmpty)))
        {
            var folderName = model.Folders.FirstOrDefault(f => f.Id == model.FolderId)?.Name;
            var description = SearchDescriber.Describe(model.Conditions, model.Operator, folderName, model.FolderPath, localizer);
            await requestLogger.LogAsync(
                OperationType.Search,
                model.CurrentSaved is { } saved
                    ? new LocalizedMessage("Log.Search.SavedQuery", saved.Name, description)
                    : new LocalizedMessage("Log.Search.Query", description));
        }

        model.Result = await searchService.SearchAsync(
            new SearchRequest(UserId, IsAdmin, model.Conditions, model.Operator, model.FolderId, model.FolderPath, model.Page, model.Sort, model.Desc),
            ct);
        return isIncremental ? PartialView("_SearchResultsPartial", model) : View(model);
    }

    /// <summary>検索条件の上限・列挙値を検証する。問題は model.Warnings に積み、問題があれば false を返す。</summary>
    private bool ValidateQuery(SearchViewModel model, List<SearchCondition> nonEmptyConditions)
    {
        var valid = true;

        // 検索条件数・文字列長の上限バリデーション（DoS耐性）
        if (nonEmptyConditions.Count > SavedSearchLimits.MaxConditions)
        {
            model.Warnings.Add(this.Translate("Error.SavedSearch.TooManyConditions", SavedSearchLimits.MaxConditions));
            valid = false;
        }

        if (nonEmptyConditions.Any(c => (c.Text?.Length ?? 0) > SavedSearchLimits.MaxConditionTextLength))
        {
            model.Warnings.Add(this.Translate("Error.SavedSearch.KeywordTooLong", SavedSearchLimits.MaxConditionTextLength));
            valid = false;
        }

        if ((model.FolderPath?.Length ?? 0) > SavedSearchLimits.MaxFolderPathLength)
        {
            model.Warnings.Add(this.Translate("Error.SavedSearch.FolderPathTooLong"));
            valid = false;
        }

        if (!Enum.IsDefined(model.Operator) || !Enum.IsDefined(model.Sort)
            || nonEmptyConditions.Any(c => !Enum.IsDefined(c.Field) || !Enum.IsDefined(c.SizeComparison) || !Enum.IsDefined(c.SizeUnit)))
        {
            model.Warnings.Add(this.Translate("Error.SavedSearch.InvalidCondition"));
            valid = false;
        }

        return valid;
    }

    /// <summary>
    /// 検索結果を CSV / TSV でダウンロードさせる。ページ送りなしで全件（設定の上限まで）を、メモリに溜めずに流す。
    /// 権限・条件は検索画面と同じ。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Export(
        SearchViewModel model,
        ExportFormat format = ExportFormat.Csv,
        ExportEncoding encoding = ExportEncoding.Utf8Bom,
        CancellationToken ct = default)
    {
        var settings = await adminSettings.GetExportAsync(ct);
        if (!IsAdmin && !settings.AllowAllUsers)
        {
            return Forbid();
        }

        var conditions = model.Conditions.Where(c => !c.IsEmpty).ToList();
        if ((ModelState.TryGetValue(nameof(format), out var formatState) && formatState.Errors.Count > 0)
            || (ModelState.TryGetValue(nameof(encoding), out var encodingState) && encodingState.Errors.Count > 0)
            || !Enum.IsDefined(format) || !Enum.IsDefined(encoding) || !ValidateQuery(model, conditions))
        {
            return BadRequest(string.Join(' ', model.Warnings.DefaultIfEmpty(this.Translate("Error.SavedSearch.InvalidCondition"))));
        }

        using var lease = exportLimiter.TryEnter();
        if (lease is null)
        {
            Response.Headers.RetryAfter = "30";
            return StatusCode(StatusCodes.Status429TooManyRequests, localizer["Search.Export.Busy"].Value);
        }

        var folders = await searchService.GetAccessibleFoldersAsync(UserId, IsAdmin, ct);
        var description = SearchDescriber.Describe(
            conditions, model.Operator, folders.FirstOrDefault(f => f.Id == model.FolderId)?.Name, model.FolderPath, localizer);
        var labels = new ExportLabels(
            [
                localizer["Search.Column.Name"].Value,
                localizer["Search.Column.Folder"].Value,
                localizer["Search.Export.Column.FullPath"].Value,
                localizer["Search.Export.Column.Type"].Value,
                localizer["Search.Column.Size"].Value,
                localizer["Search.Column.Extension"].Value,
                localizer["Search.Column.Owner"].Value,
                localizer["Search.Column.Modified"].Value,
                localizer["Search.Column.Created"].Value,
            ],
            localizer["Search.Export.Type.File"].Value,
            localizer["Search.Export.Type.Folder"].Value);

        Response.ContentType = SearchExportWriter.ContentType(format, encoding);
        Response.Headers.ContentDisposition = $"attachment; filename=\"{SearchExportWriter.FileName(format, DateTime.Now)}\"";
        Response.Headers.CacheControl = "no-store";

        var items = searchService.ExportAsync(
            new SearchRequest(UserId, IsAdmin, conditions, model.Operator, model.FolderId, model.FolderPath, 1, model.Sort, model.Desc),
            settings.MaxRows,
            ct);

        var rows = 0;
        try
        {
            rows = await SearchExportWriter.WriteAsync(Response.Body, items, format, encoding, labels, ct);
        }
        catch (OperationCanceledException)
        {
            // 受け取る側が途中でやめた。ここまでの分は送信済みなので、記録だけ残す。
            await requestLogger.LogAsync(
                OperationType.Export, new LocalizedMessage("Log.Export.Aborted", description, format.ToString().ToUpperInvariant()));
            return new EmptyResult();
        }

        await requestLogger.LogAsync(
            OperationType.Export,
            new LocalizedMessage(
                rows >= settings.MaxRows ? "Log.Export.DoneTruncated" : "Log.Export.Done",
                description,
                format.ToString().ToUpperInvariant(),
                rows));
        return new EmptyResult();
    }

    /// <summary>フォルダツリーのドリルダウン用。指定フォルダ直下のサブフォルダを返す。</summary>
    [HttpGet]
    public async Task<IActionResult> SubFolders(int folderId, string? path, CancellationToken ct) =>
        Json(await searchService.GetSubFoldersAsync(UserId, IsAdmin, folderId, path, ct));

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() =>
        View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
