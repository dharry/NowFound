using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NowFound.Application;
using NowFound.Application.Logging;
using NowFound.Application.Settings;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Web.Models;
using NowFound.Web.Resources;
using NowFound.Web.Services;

namespace NowFound.Web.Controllers;

/// <summary>操作ログの閲覧とエクスポート（管理者のみ）。</summary>
[Authorize(Roles = AppRoles.Admin)]
public class LogsController(
    IOperationLogService logs,
    IAdminSettingsService settings,
    ExportLimiter exportLimiter,
    RequestLogger requestLogger,
    IMessageTranslator translator,
    IStringLocalizer<LogsResource> localizer) : Controller
{
    public async Task<IActionResult> Index(LogSearchViewModel model, CancellationToken ct)
    {
        var pageSize = await settings.GetLogPageSizeAsync(ct);
        model.Result = await logs.SearchAsync(ToQuery(model, model.Page, pageSize), ct);
        return View(model);
    }

    /// <summary>
    /// 画面の絞り込み条件・並び順のまま、ページに関係なく全件を CSV / TSV で出力する（上限は検索結果のエクスポートと共通）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Export(
        LogSearchViewModel model,
        ExportFormat format = ExportFormat.Csv,
        ExportEncoding encoding = ExportEncoding.Utf8Bom,
        CancellationToken ct = default)
    {
        if ((ModelState.TryGetValue(nameof(format), out var formatState) && formatState.Errors.Count > 0)
            || (ModelState.TryGetValue(nameof(encoding), out var encodingState) && encodingState.Errors.Count > 0)
            || !Enum.IsDefined(format) || !Enum.IsDefined(encoding))
        {
            return BadRequest();
        }

        using var lease = exportLimiter.TryEnter();
        if (lease is null)
        {
            Response.Headers.RetryAfter = "30";
            return StatusCode(StatusCodes.Status429TooManyRequests, localizer["Logs.Export.Busy"].Value);
        }

        var exportSettings = await settings.GetExportAsync(ct);
        string[] headers =
        [
            localizer["Logs.Col.Timestamp"].Value,
            localizer["Logs.Col.Operation"].Value,
            localizer["Logs.Col.User"].Value,
            localizer["Logs.Col.Detail"].Value,
            localizer["Logs.Col.Ip"].Value,
        ];

        Response.ContentType = SearchExportWriter.ContentType(format, encoding);
        Response.Headers.ContentDisposition = $"attachment; filename=\"{LogExportWriter.FileName(format, DateTime.Now)}\"";
        Response.Headers.CacheControl = "no-store";

        var items = logs.ExportAsync(ToQuery(model, 1, 0), exportSettings.MaxRows, ct);
        var formatName = format.ToString().ToUpperInvariant();
        int rows;
        try
        {
            rows = await LogExportWriter.WriteAsync(
                Response.Body, items, format, encoding, headers,
                item => localizer[$"Logs.Operation.{item.Operation}"].Value,
                translator.TranslateLogDetail,
                ct);
        }
        catch (OperationCanceledException)
        {
            // 受け取る側が途中でやめた。ここまでの分は送信済みなので、記録だけ残す。
            await requestLogger.LogAsync(OperationType.Export, new LocalizedMessage("Log.LogExport.Aborted", formatName));
            return new EmptyResult();
        }

        await requestLogger.LogAsync(
            OperationType.Export,
            new LocalizedMessage(rows >= exportSettings.MaxRows ? "Log.LogExport.DoneTruncated" : "Log.LogExport.Done", formatName, rows));
        return new EmptyResult();
    }

    private static OperationLogQuery ToQuery(LogSearchViewModel model, int page, int pageSize) =>
        new(model.From, model.To, model.Operation, model.User, page, pageSize,
            string.IsNullOrEmpty(model.Sort) ? null : model.Sort, model.Desc);
}
