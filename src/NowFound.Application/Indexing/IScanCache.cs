namespace NowFound.Application.Indexing;

/// <summary>
/// 前回の走査結果に基づく枝刈り。ディレクトリの更新日時が前回と同じなら、
/// サブディレクトリの再帰探索のみをスキップし、既知のサブディレクトリと直下のファイル群を照合する。
/// </summary>
public interface IScanCache
{
    /// <summary>
    /// ディレクトリ構成に変化なしなら true。その場合 <paramref name="knownSubdirectories"/> に前回知っていた
    /// サブディレクトリ（フルパス）を返す。
    /// </summary>
    bool TryGetUnchanged(string directoryPath, DateTime lastWriteUtc, out IReadOnlyList<string> knownSubdirectories);
}
