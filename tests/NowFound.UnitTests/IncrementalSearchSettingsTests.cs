using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Settings;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

public class IncrementalSearchSettingsTests
{
    private static async Task<(TestHost Host, IServiceScope Scope, IAdminSettingsService Service)> CreateAsync()
    {
        var host = await TestHost.CreateAsync();
        var scope = host.Services.CreateScope();
        return (host, scope, scope.ServiceProvider.GetRequiredService<IAdminSettingsService>());
    }

    [Fact]
    public async Task Get_WhenNotConfigured_ReturnsDefaults()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var settings = await service.GetIncrementalSearchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(IncrementalSearchSettings.Default, settings);
        Assert.True(settings.IsValid);
    }

    [Fact]
    public async Task Set_ThenGet_RoundTrips_AndCanBeUpdated()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;

        var first = await service.SetIncrementalSearchAsync(new IncrementalSearchSettings(false, 3, 800), ct);
        var second = await service.SetIncrementalSearchAsync(new IncrementalSearchSettings(true, 5, 1200), ct);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(new IncrementalSearchSettings(true, 5, 1200), await service.GetIncrementalSearchAsync(ct));
    }

    [Theory]
    [InlineData(IncrementalSearchSettings.MinCharsMin, IncrementalSearchSettings.DelayMsMin)]
    [InlineData(IncrementalSearchSettings.MinCharsMax, IncrementalSearchSettings.DelayMsMax)]
    public async Task Set_AcceptsBoundaryValues(int minChars, int delayMs)
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;

        var result = await service.SetIncrementalSearchAsync(new IncrementalSearchSettings(true, minChars, delayMs), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(IncrementalSearchSettings.MinCharsMin - 1, 300, "Error.IncrementalSearch.MinCharsRange")]
    [InlineData(IncrementalSearchSettings.MinCharsMax + 1, 300, "Error.IncrementalSearch.MinCharsRange")]
    [InlineData(2, IncrementalSearchSettings.DelayMsMin - 1, "Error.IncrementalSearch.DelayRange")]
    [InlineData(2, IncrementalSearchSettings.DelayMsMax + 1, "Error.IncrementalSearch.DelayRange")]
    public async Task Set_RejectsOutOfRange_AndKeepsPreviousValue(int minChars, int delayMs, string errorKey)
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;
        await service.SetIncrementalSearchAsync(new IncrementalSearchSettings(true, 4, 500), ct);

        var result = await service.SetIncrementalSearchAsync(new IncrementalSearchSettings(false, minChars, delayMs), ct);

        Assert.False(result.Succeeded);
        Assert.Equal(errorKey, Assert.Single(result.Errors).Key);
        Assert.Equal(new IncrementalSearchSettings(true, 4, 500), await service.GetIncrementalSearchAsync(ct));
    }

    [Fact]
    public async Task Get_WhenStoredValuesAreOutOfRange_FallsBackToDefaultsButKeepsEnabledFlag()
    {
        var (host, scope, service) = await CreateAsync();
        await using var _ = host;
        using var __ = scope;
        var ct = TestContext.Current.CancellationToken;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SystemSettings.AddRange(
            new SystemSetting { Key = SystemSetting.IncrementalSearchEnabledKey, Value = "False" },
            new SystemSetting { Key = SystemSetting.IncrementalSearchMinCharsKey, Value = "0" },
            new SystemSetting { Key = SystemSetting.IncrementalSearchDelayMsKey, Value = "abc" });
        await db.SaveChangesAsync(ct);

        var settings = await service.GetIncrementalSearchAsync(ct);

        Assert.False(settings.Enabled);
        Assert.Equal(IncrementalSearchSettings.Default.MinChars, settings.MinChars);
        Assert.Equal(IncrementalSearchSettings.Default.DelayMs, settings.DelayMs);
    }
}
