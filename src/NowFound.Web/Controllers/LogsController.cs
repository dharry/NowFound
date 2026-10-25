using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NowFound.Application.Logging;
using NowFound.Application.Settings;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Web.Models;

namespace NowFound.Web.Controllers;

/// <summary>操作ログの閲覧（管理者のみ）。</summary>
[Authorize(Roles = AppRoles.Admin)]
public class LogsController(IOperationLogService logs, IAdminSettingsService settings) : Controller
{
    public async Task<IActionResult> Index(LogSearchViewModel model, CancellationToken ct)
    {
        var pageSize = await settings.GetLogPageSizeAsync(ct);
        model.Result = await logs.SearchAsync(
            new OperationLogQuery(model.From, model.To, model.Operation, model.User, model.Page, pageSize,
                string.IsNullOrEmpty(model.Sort) ? null : model.Sort, model.Desc), ct);
        return View(model);
    }
}
