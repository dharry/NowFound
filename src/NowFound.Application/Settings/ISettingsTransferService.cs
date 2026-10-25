using NowFound.Application.Users;

namespace NowFound.Application.Settings;

/// <param name="Result">失敗のとき、何も反映していない。</param>
/// <param name="Created">新しく登録した件数（フォルダのみ）。</param>
/// <param name="Updated">既存を更新した件数（フォルダのみ）。</param>
/// <param name="BindPasswordNeeded">AD の確認用アカウントを取り込んだが、パスワードが未保存（管理者が再設定する必要がある）。</param>
public sealed record ImportResult(OperationResult Result, int Created = 0, int Updated = 0, bool BindPasswordNeeded = false)
{
    public bool Succeeded => Result.Succeeded;
}

/// <summary>
/// 検索対象フォルダとシステム設定の JSON による書き出し・取り込み（テスト環境からの移行など）。
/// 取り込みは全件を検証してから反映する（1 件でも不正なら何も変更しない）。
/// </summary>
public interface ISettingsTransferService
{
    /// <summary>検索対象フォルダ（名前・パス・除外設定・共有設定）。グループへの割り当ては含まない。</summary>
    Task<string> ExportFoldersAsync(CancellationToken ct = default);

    /// <summary>パスが同じフォルダは更新、無いものは追加する。ファイルに無いフォルダは削除しない。</summary>
    Task<ImportResult> ImportFoldersAsync(string json, CancellationToken ct = default);

    /// <summary>ページサイズ・インクリメンタルサーチ・既定の除外パターン・アカウントポリシー・ActiveDirectory 設定。確認用アカウントのパスワードは含まない（取り込み後に再設定が必要）。</summary>
    Task<string> ExportSystemSettingsAsync(CancellationToken ct = default);

    Task<ImportResult> ImportSystemSettingsAsync(string json, CancellationToken ct = default);

    /// <summary>ファイルの種類（アイコン・名前・拡張子）の設定。未編集（組み込みの既定値のまま）ならその旨も書き出す。</summary>
    Task<string> ExportFileKindsAsync(CancellationToken ct = default);

    /// <summary>全体を置き換える（検証して、エラーがあれば何も変更しない）。未編集の状態のファイルなら既定値に戻す。</summary>
    Task<ImportResult> ImportFileKindsAsync(string json, CancellationToken ct = default);
}
