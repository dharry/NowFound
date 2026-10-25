namespace NowFound.Application.Indexing;

/// <summary>フォルダパスとファイル名からフルパスを組み立てる（フルパスは DB に保存しない）。</summary>
public static class FilePaths
{
    /// <summary>末尾が区切り文字のフォルダ（C:\ など）でも区切りが重複しない。スキャナーが得る FullName と一致する。</summary>
    public static string Combine(string folderPath, string name) => Path.Join(folderPath, name);
}
