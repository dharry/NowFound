using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NowFound.Application;
using NowFound.Application.FileKinds;
using NowFound.Application.Logging;
using NowFound.Web.Resources;

namespace NowFound.Web.Services;

/// <summary>サービス層が返すキー＋引数のメッセージを、現在の表示言語の文章にする。</summary>
public interface IMessageTranslator
{
    string Translate(LocalizedMessage message);

    string Translate(string key, params object?[] args);

    /// <summary>操作ログの内容。新形式（キー）は翻訳し、旧形式（文章を直接保存）はそのまま返す。</summary>
    string TranslateLogDetail(OperationLogItem item);

    /// <summary>ファイルの種類の表示名。組み込みの既定の名前は表示言語に翻訳し、管理者が付けた名前はそのまま返す。</summary>
    string FileKindLabel(FileKind kind);
}

public class MessageTranslator(IStringLocalizer<MessagesResource> localizer) : IMessageTranslator
{
    public string Translate(LocalizedMessage message) =>
        message.Key == LocalizedMessage.RawKey
            ? Convert.ToString(message.Args.FirstOrDefault()) ?? string.Empty
            : Translate(message.Key, message.Args);

    // リソースが無いキーは、キー自体（引数は {0} に展開されない）を返す。翻訳漏れに気づけるよう、そのまま表示する。
    public string Translate(string key, params object?[] args)
    {
        try
        {
            return localizer[key, [.. args.Select(a => a ?? string.Empty)]];
        }
        catch (FormatException)
        {
            // 引数の数が文言と合わない古いログなど。画面を落とさず、キーを表示する。
            return key;
        }
    }

    public string TranslateLogDetail(OperationLogItem item) =>
        item.HasDetailKey ? Translate(item.DetailKey!, [.. item.DetailArgs ?? []]) : item.Detail;

    public string FileKindLabel(FileKind kind) => kind.LabelKey is { } key ? Translate(key) : kind.Label;
}

public static class MessageTranslatorExtensions
{
    /// <summary>コントローラーからエラーを翻訳する。</summary>
    public static string Translate(this ControllerBase controller, LocalizedMessage message) =>
        controller.HttpContext.RequestServices.GetRequiredService<IMessageTranslator>().Translate(message);

    public static string Translate(this ControllerBase controller, string key, params object?[] args) =>
        controller.HttpContext.RequestServices.GetRequiredService<IMessageTranslator>().Translate(key, args);

    public static string TranslateAll(this ControllerBase controller, IEnumerable<LocalizedMessage> messages) =>
        string.Join(" ", messages.Select(m => controller.Translate(m)));
}
