using System.Net;

namespace NowFound.IntegrationTests;

public sealed class FaviconFlowTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task LayoutReferencesFaviconAssets_AndBothFormatsAreServed()
    {
        var client = _factory.CreateBrowserLikeClient();

        var login = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);
        Assert.Contains("rel=\"icon\" type=\"image/png\" sizes=\"512x512\"", login);
        Assert.Contains("/favicon.png?v=", login);
        Assert.Contains("rel=\"icon\" type=\"image/x-icon\"", login);
        Assert.Contains("/favicon.ico?v=", login);

        var png = await client.GetAsync("/favicon.png", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        var pngBytes = await png.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(pngBytes.Length > 10_000);
        Assert.Equal([0x89, 0x50, 0x4e, 0x47], pngBytes[..4]);

        var ico = await client.GetAsync("/favicon.ico", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ico.StatusCode);
        Assert.StartsWith("image/", ico.Content.Headers.ContentType?.MediaType);
        var icoBytes = await ico.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(icoBytes.Length > 1_000);
        Assert.Equal([0x00, 0x00, 0x01, 0x00], icoBytes[..4]);
    }
}
