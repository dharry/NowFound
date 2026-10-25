using NowFound.Application.Information;

namespace NowFound.Web.Models;

/// <summary>情報画面。<paramref name="Details"/> は管理者のときだけ設定される（ホスト詳細・DB）。</summary>
public sealed record InformationViewModel(string MachineName, RuntimeSummary Runtime, HostInfo? Details);
