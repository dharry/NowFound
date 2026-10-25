using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Settings;

namespace NowFound.UnitTests;

public class ExportSettingsTests
{
    private static async Task<(TestHost Host, IServiceScope Scope, IAdminSettingsService Service)> CreateAsync()
    {
        var host = await TestHost.CreateAsync();
        var scope = host.Services.CreateScope();
        return (host, scope, scope.ServiceProvider.GetRequiredService<IAdminSettingsService>());
    }

    [Fact]
    public async Task Get_WhenNotConfigured_IsAdminsOnly_WithOneMillionRows()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var settings = await service.GetExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ExportSettings(false, 1_000_000), settings);
    }

    [Fact]
    public async Task Set_ThenGet_RoundTrips_AndCanBeUpdated()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;

        Assert.True((await service.SetExportAsync(new ExportSettings(true, 500), ct)).Succeeded);
        Assert.Equal(new ExportSettings(true, 500), await service.GetExportAsync(ct));

        Assert.True((await service.SetExportAsync(new ExportSettings(false, 2_000_000), ct)).Succeeded);
        Assert.Equal(new ExportSettings(false, 2_000_000), await service.GetExportAsync(ct));
    }

    [Theory]
    [InlineData(ExportSettings.MaxRowsMin)]
    [InlineData(ExportSettings.MaxRowsMax)]
    public async Task Set_AcceptsBoundaryValues(int rows)
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        Assert.True((await service.SetExportAsync(new ExportSettings(true, rows), TestContext.Current.CancellationToken)).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(ExportSettings.MaxRowsMax + 1)]
    public async Task Set_RejectsOutOfRange_AndKeepsTheOldValue(int rows)
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;
        await service.SetExportAsync(new ExportSettings(true, 123), ct);

        var result = await service.SetExportAsync(new ExportSettings(false, rows), ct);

        Assert.False(result.Succeeded);
        Assert.Equal("Error.Export.MaxRowsRange", result.Errors.Single().Key);
        Assert.Equal(new ExportSettings(true, 123), await service.GetExportAsync(ct));
    }
}
