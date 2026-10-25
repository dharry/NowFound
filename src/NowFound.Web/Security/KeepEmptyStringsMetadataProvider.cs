using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace NowFound.Web.Security;

/// <summary>
/// 空文字を null に変換しない。ビューモデルの非 null 文字列プロパティ（Nullable 有効）に
/// 空のフォーム入力が null として入り、NullReferenceException になるのを防ぐ。
/// </summary>
public class KeepEmptyStringsMetadataProvider : IDisplayMetadataProvider
{
    public void CreateDisplayMetadata(DisplayMetadataProviderContext context) =>
        context.DisplayMetadata.ConvertEmptyStringToNull = false;
}
