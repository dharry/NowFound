using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

public class ActiveDirectoryLookupRulesTests
{
    private static readonly ActiveDirectorySettings Base = new(true, "dc01", 389, DirectorySecurity.None, "corp.example.com");

    [Fact]
    public void EffectiveSearchBase_IsDerivedFromTheDomain_WhenNotSpecified() =>
        Assert.Equal("DC=corp,DC=example,DC=com", ActiveDirectoryRules.EffectiveSearchBase(Base));

    [Fact]
    public void EffectiveSearchBase_UsesTheSpecifiedDn() =>
        Assert.Equal(
            "OU=Staff,DC=corp,DC=example,DC=com",
            ActiveDirectoryRules.EffectiveSearchBase(Base with { SearchBase = "OU=Staff,DC=corp,DC=example,DC=com" }));

    [Fact]
    public void EffectiveSearchBase_WithoutDomain_IsEmpty() =>
        Assert.Equal(string.Empty, ActiveDirectoryRules.EffectiveSearchBase(Base with { Domain = string.Empty }));

    [Theory]
    [InlineData("svc-lookup", "svc-lookup@corp.example.com")]
    [InlineData("svc@other.test", "svc@other.test")]
    [InlineData("CN=svc,OU=Svc,DC=corp,DC=example,DC=com", "CN=svc,OU=Svc,DC=corp,DC=example,DC=com")]
    [InlineData("CORP\\svc", "CORP\\svc")]
    public void EffectiveBindName_AppendsTheDomainOnlyToPlainUserNames(string bindUser, string expected) =>
        Assert.Equal(expected, ActiveDirectoryRules.EffectiveBindName(Base with { BindUser = bindUser }));

    [Theory]
    [InlineData("kishi", "kishi")]
    [InlineData("a*", "a\\2a")]
    [InlineData("a(b)", "a\\28b\\29")]
    [InlineData("a\\b", "a\\5cb")]
    [InlineData("*)(uid=*))(|(uid=*", "\\2a\\29\\28uid=\\2a\\29\\29\\28|\\28uid=\\2a")]
    public void EscapeFilterValue_EscapesLdapFilterCharacters(string input, string expected) =>
        Assert.Equal(expected, ActiveDirectoryRules.EscapeFilterValue(input));

    [Theory]
    [InlineData("DC=corp,DC=example,DC=com", true)]
    [InlineData("OU=a,DC=b", true)]
    [InlineData("corp.example.com", false)]
    [InlineData("", false)]
    [InlineData("DC=a\n,DC=b", false)]
    public void IsValidSearchBase_ChecksTheShapeOfADn(string value, bool expected) =>
        Assert.Equal(expected, ActiveDirectoryRules.IsValidSearchBase(value));

    [Fact]
    public void Validate_BadSearchBase_Fails() =>
        Assert.Equal(
            "Error.Ad.SearchBaseInvalid",
            Assert.Single(ActiveDirectoryRules.Validate(Base with { SearchBase = "not a dn" }).Errors).Key);

    [Fact]
    public void Validate_BindUserWithControlCharacters_Fails() =>
        Assert.Equal(
            "Error.Ad.BindUserInvalid",
            Assert.Single(ActiveDirectoryRules.Validate(Base with { BindUser = "svc\n" }).Errors).Key);
}

public class ActiveDirectoryBindPasswordTests
{
    private static readonly ActiveDirectorySettings Settings = new(true, "dc01", 389, DirectorySecurity.None, "corp.test", "svc-lookup");

    [Fact]
    public async Task BindPassword_IsStoredEncrypted_AndRoundTrips()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IActiveDirectorySettingsService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await service.SetAsync(Settings, "S3cret-Pass!", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(await service.HasBindPasswordAsync(TestContext.Current.CancellationToken));
        Assert.Equal("S3cret-Pass!", await service.GetBindPasswordAsync(TestContext.Current.CancellationToken));
        var stored = db.SystemSettings.Single(x => x.Key == SystemSetting.AdBindPasswordKey).Value;
        Assert.DoesNotContain("S3cret-Pass!", stored);
        Assert.Equal("svc-lookup", (await service.GetAsync(TestContext.Current.CancellationToken)).BindUser);
    }

    [Fact]
    public async Task BlankPassword_KeepsTheStoredOne_AndANewOneReplacesIt()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IActiveDirectorySettingsService>();
        await service.SetAsync(Settings, "first", TestContext.Current.CancellationToken);

        await service.SetAsync(Settings with { Server = "dc02" }, string.Empty, TestContext.Current.CancellationToken);
        Assert.Equal("first", await service.GetBindPasswordAsync(TestContext.Current.CancellationToken));

        await service.SetAsync(Settings, "second", TestContext.Current.CancellationToken);
        Assert.Equal("second", await service.GetBindPasswordAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearingTheBindUser_DeletesTheStoredPassword()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IActiveDirectorySettingsService>();
        await service.SetAsync(Settings, "first", TestContext.Current.CancellationToken);

        await service.SetAsync(Settings with { BindUser = string.Empty }, null, TestContext.Current.CancellationToken);

        Assert.False(await service.HasBindPasswordAsync(TestContext.Current.CancellationToken));
        Assert.Null(await service.GetBindPasswordAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BindUserWithoutAnyPassword_IsRejected()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IActiveDirectorySettingsService>();

        var result = await service.SetAsync(Settings, null, TestContext.Current.CancellationToken);

        Assert.Equal("Error.Ad.BindPasswordRequired", Assert.Single(result.Errors).Key);
        Assert.Equal(string.Empty, (await service.GetAsync(TestContext.Current.CancellationToken)).BindUser);
    }

    [Fact]
    public async Task UndecryptableStoredPassword_IsTreatedAsMissing()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IActiveDirectorySettingsService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await service.SetAsync(Settings, "first", TestContext.Current.CancellationToken);
        db.SystemSettings.Single(x => x.Key == SystemSetting.AdBindPasswordKey).Value = "CfDJ8-broken";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await service.GetBindPasswordAsync(TestContext.Current.CancellationToken));
    }
}

public class LdapLookupTests
{
    private static readonly ActiveDirectorySettings Configured = new(true, "127.0.0.1", 1, DirectorySecurity.None, "corp.test");

    private static LdapDirectoryAuthenticator Create() => new(NullLogger<LdapDirectoryAuthenticator>.Instance);

    [Fact]
    public async Task CheckConnection_WithoutServer_IsNotConfigured()
    {
        var result = await Create().CheckConnectionAsync(ActiveDirectorySettings.Default, null, TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryCheckStatus.NotConfigured, result.Status);
    }

    [Fact]
    public async Task CheckConnection_UnreachableServer_FailsWithADetail()
    {
        var result = await Create().CheckConnectionAsync(Configured, null, TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryCheckStatus.ConnectFailed, result.Status);
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("svc", "")]
    public async Task FindUser_WithoutALookupAccount_IsNotConfigured(string bindUser, string bindPassword)
    {
        var result = await Create().FindUserAsync(
            Configured with { BindUser = bindUser }, bindPassword, "kishi", TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryLookupStatus.NotConfigured, result.Status);
    }

    [Fact]
    public async Task FindUser_UnreachableServer_IsAConnectionFailure()
    {
        var result = await Create().FindUserAsync(
            Configured with { BindUser = "svc" }, "pw", "kishi", TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryLookupStatus.ConnectFailed, result.Status);
    }
}
