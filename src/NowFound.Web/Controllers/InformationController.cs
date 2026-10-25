using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NowFound.Application.Information;
using NowFound.Domain;
using NowFound.Web.Models;

namespace NowFound.Web.Controllers;

/// <summary>稼働ホスト・.NET・データベースの情報表示。一般ユーザーにはコンピュータ名と .NET だけ、管理者には全情報を見せる。</summary>
[Authorize]
public class InformationController(IHostInfoService hostInfo) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var basic = hostInfo.GetBasic();
        var details = User.IsInRole(AppRoles.Admin) ? await hostInfo.GetAsync(ct) : null;
        return View(new InformationViewModel(basic.MachineName, basic.Runtime, details));
    }
}
