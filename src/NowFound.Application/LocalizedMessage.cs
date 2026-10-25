using System.Globalization;

namespace NowFound.Application;

/// <summary>
/// 表示言語に依らない通知・エラーメッセージ（リソースのキー＋引数）。
/// サービス層は文言を持たず、Web 層が表示時に翻訳する。
/// </summary>
public sealed record LocalizedMessage(string Key, params object?[] Args)
{
    /// <summary>翻訳せずそのまま表示する文字列（Identity のエラーなど、既に翻訳済みのもの）を表すキー。</summary>
    public const string RawKey = "!raw";

    public static LocalizedMessage Raw(string text) => new(RawKey, text);

    public static implicit operator LocalizedMessage(string key) => new(key);

    /// <summary>操作ログに保存するため、引数をカルチャに依存しない文字列にする。</summary>
    public IReadOnlyList<string> FormatArgs() =>
        [.. Args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture) ?? string.Empty)];
}
