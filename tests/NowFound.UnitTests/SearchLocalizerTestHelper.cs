using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NowFound.Web.Resources;

namespace NowFound.UnitTests;

/// <summary>実際の SearchResource（resx）を使い、指定した表示言語で何かを実行するテスト用ヘルパー。</summary>
internal static class SearchLocalizerTestHelper
{
    public static IStringLocalizer<SearchResource> Create() =>
        new StringLocalizer<SearchResource>(
            new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));

    /// <summary>表示言語（CurrentUICulture）を一時的に切り替えて実行する。</summary>
    public static T InCulture<T>(string culture, Func<IStringLocalizer<SearchResource>, T> action)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return action(Create());
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    public static T Japanese<T>(Func<IStringLocalizer<SearchResource>, T> action) => InCulture("ja", action);
}
