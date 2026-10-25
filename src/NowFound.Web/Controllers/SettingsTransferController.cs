using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NowFound.Application;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Web.Resources;
using NowFound.Web.Services;

namespace NowFound.Web.Controllers;

/// <summary>検索対象フォルダとシステム設定の JSON 書き出し・取り込み。システム管理者のみ。</summary>
[Authorize(Roles = AppRoles.Admin)]
public class SettingsTransferController(
    ISettingsTransferService transfer,
    RequestLogger requestLogger,
    IStringLocalizer<SettingsResource> localizer) : Controller
{
    /// <summary>取り込みファイルの上限。設定ファイルとしては十分に大きい値。</summary>
    internal const int MaxFileBytes = 1024 * 1024;

    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet]
    public async Task<IActionResult> ExportFolders(CancellationToken ct)
    {
        var json = await transfer.ExportFoldersAsync(ct);
        await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Transfer.FoldersExported"));
        return JsonFile(json, "nowfound-folders");
    }

    [HttpGet]
    public async Task<IActionResult> ExportSystemSettings(CancellationToken ct)
    {
        var json = await transfer.ExportSystemSettingsAsync(ct);
        await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Transfer.SystemExported"));
        return JsonFile(json, "nowfound-system-settings");
    }

    [HttpGet]
    public async Task<IActionResult> ExportFileKinds(CancellationToken ct)
    {
        var json = await transfer.ExportFileKindsAsync(ct);
        await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Transfer.FileKindsExported"));
        return JsonFile(json, "nowfound-file-kinds");
    }

    [HttpPost]
    [RequestSizeLimit(MaxFileBytes + 64 * 1024)]
    public async Task<IActionResult> ImportFileKinds(IFormFile? file, CancellationToken ct)
    {
        var (json, error) = await ReadAsync(file, ct);
        if (json is null)
        {
            return Failed(error);
        }

        var result = await transfer.ImportFileKindsAsync(json, ct);
        if (!result.Succeeded)
        {
            return Failed(result.Result);
        }

        await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Transfer.FileKindsImported"));
        TempData["Message"] = localizer["Transfer.FileKindsImported"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [RequestSizeLimit(MaxFileBytes + 64 * 1024)]
    public async Task<IActionResult> ImportFolders(IFormFile? file, CancellationToken ct)
    {
        var (json, error) = await ReadAsync(file, ct);
        if (json is null)
        {
            return Failed(error);
        }

        var result = await transfer.ImportFoldersAsync(json, ct);
        if (!result.Succeeded)
        {
            return Failed(result.Result);
        }

        await requestLogger.LogAsync(
            OperationType.AdminChange, new LocalizedMessage("Log.Transfer.FoldersImported", result.Created, result.Updated));
        TempData["Message"] = localizer["Transfer.FoldersImported", result.Created, result.Updated].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [RequestSizeLimit(MaxFileBytes + 64 * 1024)]
    public async Task<IActionResult> ImportSystemSettings(IFormFile? file, CancellationToken ct)
    {
        var (json, error) = await ReadAsync(file, ct);
        if (json is null)
        {
            return Failed(error);
        }

        var result = await transfer.ImportSystemSettingsAsync(json, ct);
        if (!result.Succeeded)
        {
            return Failed(result.Result);
        }

        await requestLogger.LogAsync(OperationType.AdminChange, new LocalizedMessage("Log.Transfer.SystemImported"));
        TempData["Message"] = localizer["Transfer.SystemImported"].Value;
        if (result.BindPasswordNeeded)
        {
            TempData["Warning"] = localizer["Transfer.BindPasswordNeeded"].Value;
        }

        return RedirectToAction(nameof(Index));
    }

    private FileContentResult JsonFile(string json, string baseName) =>
        File(Encoding.UTF8.GetBytes(json), "application/json", $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}.json");

    private async Task<(string? Json, OperationResult? Error)> ReadAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return (null, OperationResult.Failure("Error.Transfer.NoFile"));
        }

        if (file.Length > MaxFileBytes)
        {
            return (null, OperationResult.Failure("Error.Transfer.TooLarge", MaxFileBytes / 1024));
        }

        using var reader = new StreamReader(file.OpenReadStream(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return (await reader.ReadToEndAsync(ct), null);
    }

    private IActionResult Failed(OperationResult? result)
    {
        TempData["Error"] = this.TranslateAll(result?.Errors ?? []);
        return RedirectToAction(nameof(Index));
    }
}
