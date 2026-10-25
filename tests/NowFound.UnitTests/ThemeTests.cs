using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Users;
using NowFound.Domain;
using NowFound.Web.Security;

namespace NowFound.UnitTests;

public partial class ThemeTests
{
    /// <summary>テーマが定義すべきトークン。</summary>
    private static readonly string[] RequiredTokens =
    [
        "bg", "surface", "surface-container", "surface-container-high",
        "on-surface", "on-surface-variant", "outline", "outline-variant",
        "primary", "on-primary", "primary-container", "on-primary-container",
        "secondary-container", "on-secondary-container",
        "error", "on-error", "success", "scrim", "shadow",
        "table-header", "on-table-header",
    ];

    /// <summary>(前景, 背景, 最小コントラスト比)。テキストは WCAG AA の 4.5、枠線などの UI 部品は 3.0。</summary>
    private static readonly (string Fg, string Bg, double Min)[] ContrastPairs =
    [
        ("on-surface", "surface", 4.5), ("on-surface", "bg", 4.5),
        ("on-surface", "surface-container", 4.5), ("on-surface", "surface-container-high", 4.5),
        ("on-surface-variant", "surface", 4.5), ("on-surface-variant", "bg", 4.5),
        ("on-surface-variant", "surface-container", 4.5),
        ("on-primary", "primary", 4.5), ("primary", "surface", 4.5), ("primary", "bg", 4.5),
        ("primary", "surface-container", 4.5),
        ("on-primary-container", "primary-container", 4.5),
        ("on-secondary-container", "secondary-container", 4.5),
        ("error", "surface", 4.5), ("error", "bg", 4.5), ("on-error", "error", 4.5),
        ("success", "surface", 4.5), ("on-table-header", "table-header", 4.5),
        ("outline", "surface", 3.0), ("outline", "bg", 3.0),
    ];

    private static string ThemesCssPath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src", "NowFound.Web", "wwwroot", "css", "themes.css"));

    [GeneratedRegex(@"\[data-theme=""(?<id>[\w-]+)""\][^{]*\{(?<body>[^}]*)\}")]
    private static partial Regex ThemeBlockRegex();

    [GeneratedRegex(@"--md-(?<name>[\w-]+):\s*(?<value>#[0-9a-fA-F]{6})\s*;")]
    private static partial Regex TokenRegex();

    private static Dictionary<string, Dictionary<string, string>> LoadThemes()
    {
        var css = File.ReadAllText(ThemesCssPath());
        return ThemeBlockRegex().Matches(css).ToDictionary(
            m => m.Groups["id"].Value,
            m => TokenRegex().Matches(m.Groups["body"].Value).ToDictionary(t => t.Groups["name"].Value, t => t.Groups["value"].Value));
    }

    private static double Luminance(string hex)
    {
        double Channel(int start)
        {
            var c = int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(1)) + (0.7152 * Channel(3)) + (0.0722 * Channel(5));
    }

    private static double Contrast(string a, string b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void AppThemes_HasVisualStudioAndVimThemes_WithUniqueIds()
    {
        var ids = AppThemes.All.Select(t => t.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains(AppThemes.Default, ids);
        Assert.Equal(["light", "dark", "blue", "solarized", "desert", "hybrid", "slack", "facebook", "teams", "discord"], ids);
    }

    [Theory]
    [InlineData("Resource.resx")]
    [InlineData("Resource.en.resx")]
    public void EveryTheme_HasNameAndDescription_InTheAccountResource(string suffix)
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThemesCssPath())!, "..", "..", "Resources", "Account" + suffix));
        var keys = XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

        foreach (var theme in AppThemes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(keys.GetValueOrDefault($"Theme.{theme.Id}.Name")), $"{theme.Id}: Name がありません");
            Assert.False(string.IsNullOrWhiteSpace(keys.GetValueOrDefault($"Theme.{theme.Id}.Description")), $"{theme.Id}: Description がありません");
        }
    }

    [Theory]
    [InlineData("dark", "dark")]
    [InlineData("desert", "desert")]
    [InlineData(null, "light")]
    [InlineData("", "light")]
    [InlineData("DARK", "light")]
    [InlineData("nope", "light")]
    [InlineData("dark\"><script>", "light")]
    public void Normalize_FallsBackToDefault(string? input, string expected)
    {
        Assert.Equal(expected, AppThemes.Normalize(input));
    }

    [Fact]
    public void ThemesCss_DefinesEveryAppTheme_AndNothingElse()
    {
        var themes = LoadThemes();

        Assert.Equal(AppThemes.All.Select(t => t.Id).Order(), themes.Keys.Order());
    }

    [Fact]
    public void ThemesCss_EveryThemeDefinesAllTokens()
    {
        foreach (var (id, tokens) in LoadThemes())
        {
            var missing = RequiredTokens.Except(tokens.Keys).ToList();
            Assert.True(missing.Count == 0, $"{id}: 未定義のトークン {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void ThemesCss_AllThemesMeetContrastRequirements()
    {
        var failures = new List<string>();
        foreach (var (id, tokens) in LoadThemes())
        {
            foreach (var (fg, bg, min) in ContrastPairs)
            {
                var ratio = Contrast(tokens[fg], tokens[bg]);
                if (ratio < min)
                {
                    failures.Add($"{id}: {fg} {tokens[fg]} / {bg} {tokens[bg]} = {ratio:F2} (必要 {min})");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ThemesCss_DarkFlagMatchesBackgroundLuminance()
    {
        var themes = LoadThemes();

        foreach (var theme in AppThemes.All)
        {
            var isDarkBackground = Luminance(themes[theme.Id]["bg"]) < 0.18;
            Assert.True(theme.IsDark == isDarkBackground, $"{theme.Id}: IsDark と背景色が一致しない");
        }
    }

    [Fact]
    public async Task UserTheme_DefaultsToNull_SetsAndRejectsInvalid()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var userId = (await users.ListAsync(TestContext.Current.CancellationToken)).Single().Id;

        Assert.Null(await users.GetThemeAsync(userId, TestContext.Current.CancellationToken));
        Assert.True((await users.SetThemeAsync(userId, "desert", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal("desert", await users.GetThemeAsync(userId, TestContext.Current.CancellationToken));

        Assert.False((await users.SetThemeAsync(userId, "no-such-theme", TestContext.Current.CancellationToken)).Succeeded);
        Assert.False((await users.SetThemeAsync(userId, "", TestContext.Current.CancellationToken)).Succeeded);
        Assert.False((await users.SetThemeAsync("ghost", "dark", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal("desert", await users.GetThemeAsync(userId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Resolve_Anonymous_UsesCookieButOnlyIfValid()
    {
        static string Resolve(string? cookie)
        {
            var context = new DefaultHttpContext();
            if (cookie is not null)
            {
                context.Request.Headers.Cookie = $"{ThemeHelper.CookieName}={cookie}";
            }

            return ThemeHelper.Resolve(context);
        }

        Assert.Equal("light", Resolve(null));
        Assert.Equal("hybrid", Resolve("hybrid"));
        Assert.Equal("light", Resolve("evil\"><script>alert(1)</script>"));
        Assert.Equal("light", Resolve("unknown"));
    }

    [Fact]
    public void Resolve_Authenticated_UsesClaimNotCookie()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AppUserClaimsPrincipalFactory.ThemeClaim, "blue")], "test")),
        };
        context.Request.Headers.Cookie = $"{ThemeHelper.CookieName}=dark";

        Assert.Equal("blue", ThemeHelper.Resolve(context));
    }

    [Fact]
    public void Resolve_AuthenticatedWithoutValidClaim_FallsBackToDefault()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AppUserClaimsPrincipalFactory.ThemeClaim, "garbage")], "test")),
        };

        Assert.Equal("light", ThemeHelper.Resolve(context));
    }
}
