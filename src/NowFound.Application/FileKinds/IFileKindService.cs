using NowFound.Application.Users;

namespace NowFound.Application.FileKinds;

/// <summary>ファイルの種類（アイコン・名前・拡張子）の設定。管理者が編集し、検索結果の表示に使う。</summary>
public interface IFileKindService
{
    /// <summary>表示用のカタログ。設定がまだ無ければ組み込みの既定値。</summary>
    Task<FileKindCatalog> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>編集画面に出す定義（フォルダ・汎用のファイルが先頭）。設定がまだ無ければ組み込みの既定値。</summary>
    Task<IReadOnlyList<FileKindDefinition>> GetDefinitionsAsync(CancellationToken ct = default);

    /// <summary>管理者が編集済みか（false なら組み込みの既定値を使っている）。</summary>
    Task<bool> IsCustomizedAsync(CancellationToken ct = default);

    /// <summary>検証して全体を置き換える。エラーがあれば何も変更しない。</summary>
    Task<OperationResult> SaveAsync(IReadOnlyList<FileKindEdit> edits, CancellationToken ct = default);

    /// <summary>編集内容を破棄して、組み込みの既定値に戻す。</summary>
    Task ResetAsync(CancellationToken ct = default);
}
