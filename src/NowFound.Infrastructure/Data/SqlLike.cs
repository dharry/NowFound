namespace NowFound.Infrastructure.Data;

/// <summary>LIKE の部分一致パターン。入力中の % _ \ は文字として扱う（EF.Functions.Like(..., pattern, "\\") と組み合わせる）。</summary>
public static class SqlLike
{
    public const string Escape = "\\";

    public static string Contains(string text) =>
        "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
