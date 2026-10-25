using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NowFound.IntegrationTests;

public sealed class ForwardedHeadersTests : IDisposable
{
    private readonly WebAppFactory _defaultFactory = new();

    public void Dispose()
    {
        _defaultFactory.Dispose();
    }

    [Fact]
    public void DefaultConfiguration_DisablesForwardedHeaders()
    {
        using var scope = _defaultFactory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
    }

    [Fact]
    public void WhenKnownProxiesConfigured_EnablesForwardedHeadersWithConfiguredProxies()
    {
        using var factory = _defaultFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ForwardedHeaders:KnownProxies:0", "198.51.100.1");
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/8");
        });

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.All, options.ForwardedHeaders);
        Assert.Contains(System.Net.IPAddress.Parse("198.51.100.1"), options.KnownProxies);
        Assert.Contains(System.Net.IPNetwork.Parse("10.0.0.0/8"), options.KnownIPNetworks);
    }

    [Fact]
    public async Task WhenNoProxiesConfigured_XForwardedForHeader_IsNotTrusted()
    {
        var client = _defaultFactory.CreateBrowserLikeClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login");
        request.Headers.Add("X-Forwarded-For", "203.0.113.195");
        request.Headers.Add("X-Forwarded-Proto", "http");

        var html = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);
        var tokenMatch = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        request.Content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", tokenMatch.Groups[1].Value),
            new("Email", "spoof-test@example.com"),
            new("Password", "wrong-password")
        ]);

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        await _defaultFactory.Services.GetRequiredService<NowFound.Application.Logging.IOperationLogQueue>().FlushAsync(TestContext.Current.CancellationToken);

        using var scope = _defaultFactory.Services.CreateScope();
        var opeDb = scope.ServiceProvider.GetRequiredService<NowFound.Infrastructure.Data.OperationLogDbContext>();
        var entry = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(opeDb.OperationLogs)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync(l => l.UserEmail == "spoof-test@example.com", TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.NotEqual("203.0.113.195", entry.IpAddress);
    }
}

