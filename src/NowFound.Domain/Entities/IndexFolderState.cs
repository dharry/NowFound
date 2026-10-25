namespace NowFound.Domain.Entities;

/// <summary>検索対象フォルダごとの Indexer 実行状態（files.db）。</summary>
public class IndexFolderState
{
    public int SearchFolderId { get; set; }

    /// <summary>前回走査時の収集設定（パス・隠し除外・除外パターン）のハッシュ。変わったらフル走査にする。</summary>
    public string SettingsHash { get; set; } = string.Empty;

    public DateTime LastScanUtc { get; set; }

    public DateTime LastFullScanUtc { get; set; }
}
