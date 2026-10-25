using System.Text.RegularExpressions;

namespace NowFound.IntegrationTests;

/// <summary>CSRF トークン付きのフォーム送信とログインを行うテスト用ヘルパー。</summary>
public static partial class WebTestClient
{
    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    public static async Task<HttpResponseMessage> PostFormAsync(
        this HttpClient client, string url, string tokenPage, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var html = await client.GetStringAsync(tokenPage);
        var token = TokenRegex().Match(html).Groups[1].Value;
        var all = fields.Append(new("__RequestVerificationToken", token));
        return await client.PostAsync(url, new FormUrlEncodedContent(all));
    }

    public static async Task LoginAsync(this HttpClient client, string email, string password)
    {
        var response = await client.PostFormAsync("/Account/Login", "/Account/Login",
            [new("Email", email), new("Password", password)]);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>ユーザー設定の「表示言語」を変更する（テーマは既定の明るいまま、ページサイズは既定値）。</summary>
    public static async Task SetLanguageAsync(this HttpClient client, string language)
    {
        var response = await client.PostFormAsync("/Account/Preferences", "/Account/Preferences",
            [new("Theme", "light"), new("PageSize", ""), new("Language", language)]);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>初期管理者でログインし、強制されるパスワード変更まで済ませる。</summary>
    public static async Task LoginAsAdminAsync(this HttpClient client)
    {
        await client.LoginAsync("admin@nowfound.invalid", "password123!");
        var response = await client.PostFormAsync("/Account/ChangePassword", "/Account/ChangePassword",
        [
            new("CurrentPassword", "password123!"),
            new("NewPassword", "NewPassw0rd!"),
            new("ConfirmPassword", "NewPassw0rd!"),
        ]);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }
}
