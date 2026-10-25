using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NowFound.Application.Users;
using NowFound.Domain;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

public class ActiveDirectoryRulesTests
{
    [Theory]
    [InlineData("kishi", "kishi")]
    [InlineData("  kishi  ", "kishi")]
    [InlineData(@"CORP\kishi", "kishi")]
    [InlineData("first.last", "first.last")]
    [InlineData("svc-app$", "svc-app$")]
    public void NormalizeUserName_AcceptsSamAccountNames(string input, string expected) =>
        Assert.Equal(expected, ActiveDirectoryRules.NormalizeUserName(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kishi@corp.example.com")]
    [InlineData("a b")]
    [InlineData("a,b")]
    [InlineData("a=b")]
    [InlineData("a*")]
    [InlineData(@"CORP\")]
    [InlineData("abcdefghijklmnopqrstu")]
    public void NormalizeUserName_RejectsInvalid(string input) =>
        Assert.Null(ActiveDirectoryRules.NormalizeUserName(input));

    [Fact]
    public void NormalizeUserName_Null_ReturnsNull() => Assert.Null(ActiveDirectoryRules.NormalizeUserName(null));

    [Theory]
    [InlineData("dc01", true)]
    [InlineData("dc01.corp.example.com", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("", false)]
    [InlineData("-dc", false)]
    [InlineData("dc 01", false)]
    [InlineData("dc01/evil", false)]
    [InlineData("ldap://dc01", false)]
    public void IsValidHost_ChecksHostNames(string host, bool expected) =>
        Assert.Equal(expected, ActiveDirectoryRules.IsValidHost(host));

    [Fact]
    public void Validate_DisabledWithBlankFields_IsAllowed() =>
        Assert.True(ActiveDirectoryRules.Validate(ActiveDirectorySettings.Default).Succeeded);

    [Fact]
    public void Validate_EnabledWithoutServerOrDomain_Fails()
    {
        var result = ActiveDirectoryRules.Validate(ActiveDirectorySettings.Default with { Enabled = true });

        Assert.Equal("Error.Ad.Incomplete", Assert.Single(result.Errors).Key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validate_PortOutOfRange_Fails(int port)
    {
        var result = ActiveDirectoryRules.Validate(ActiveDirectorySettings.Default with { Port = port });

        Assert.Equal("Error.Ad.PortRange", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public void Validate_BadServerOrDomain_Fails()
    {
        Assert.Equal(
            "Error.Ad.ServerInvalid",
            Assert.Single(ActiveDirectoryRules.Validate(ActiveDirectorySettings.Default with { Server = "a b" }).Errors).Key);
        Assert.Equal(
            "Error.Ad.DomainInvalid",
            Assert.Single(ActiveDirectoryRules.Validate(ActiveDirectorySettings.Default with { Domain = "corp/x" }).Errors).Key);
    }

    [Fact]
    public void Validate_UnknownSecurity_Fails()
    {
        var result = ActiveDirectoryRules.Validate(ActiveDirectorySettings.Default with { Security = (DirectorySecurity)99 });

        Assert.Equal("Error.Ad.SecurityInvalid", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public void IsConfigured_RequiresEnabledServerAndDomain()
    {
        var full = new ActiveDirectorySettings(true, "dc01", 389, DirectorySecurity.None, "corp.test");

        Assert.True(full.IsConfigured);
        Assert.False(full with { Enabled = false } is { IsConfigured: true });
        Assert.False(full with { Server = string.Empty } is { IsConfigured: true });
        Assert.False(full with { Domain = string.Empty } is { IsConfigured: true });
    }

    [Theory]
    [InlineData(DirectorySecurity.None, 389)]
    [InlineData(DirectorySecurity.StartTls, 389)]
    [InlineData(DirectorySecurity.Ssl, 636)]
    public void DefaultPort_FollowsSecurity(DirectorySecurity security, int port) =>
        Assert.Equal(port, ActiveDirectorySettings.DefaultPort(security));

    [Theory]
    [InlineData("a@example.com", true)]
    [InlineData("kishi", false)]
    [InlineData("", false)]
    [InlineData("a@", false)]
    public void EmailRules_DistinguishesEmailsFromUserNames(string value, bool expected) =>
        Assert.Equal(expected, EmailRules.IsValid(value));

    [Fact]
    public void AuthSources_NormalizeTreatsUnknownAsLocal()
    {
        Assert.Equal(AuthSources.Local, AuthSources.Normalize(null));
        Assert.Equal(AuthSources.Local, AuthSources.Normalize("x"));
        Assert.Equal(AuthSources.ActiveDirectory, AuthSources.Normalize(AuthSources.ActiveDirectory));
        Assert.True(AuthSources.IsValid(AuthSources.Local));
        Assert.False(AuthSources.IsValid("x"));
    }
}

public class ActiveDirectorySettingsServiceTests
{
    [Fact]
    public async Task Get_Initially_ReturnsDisabledDefaults()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();

        var settings = await scope.ServiceProvider.GetRequiredService<IActiveDirectorySettingsService>().GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ActiveDirectorySettings.Default, settings);
    }

    [Fact]
    public async Task Set_RoundTrips_AndTrimsInput()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IActiveDirectorySettingsService>();

        var result = await service.SetAsync(
            new ActiveDirectorySettings(true, " dc01.corp.test ", 636, DirectorySecurity.Ssl, " corp.test "), ct: TestContext.Current.CancellationToken);
        var saved = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(new ActiveDirectorySettings(true, "dc01.corp.test", 636, DirectorySecurity.Ssl, "corp.test"), saved);
    }

    [Fact]
    public async Task Set_Invalid_IsRejectedAndNotSaved()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IActiveDirectorySettingsService>();

        var result = await service.SetAsync(ActiveDirectorySettings.Default with { Enabled = true }, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False((await service.GetAsync(TestContext.Current.CancellationToken)).Enabled);
    }

    [Fact]
    public async Task Get_WithTamperedValues_FallsBackToDisabledDefaults()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SystemSettings.AddRange(
            new SystemSetting { Key = SystemSetting.AdEnabledKey, Value = "true" },
            new SystemSetting { Key = SystemSetting.AdServerKey, Value = "dc01/../evil" },
            new SystemSetting { Key = SystemSetting.AdDomainKey, Value = "corp.test" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var settings = await scope.ServiceProvider.GetRequiredService<IActiveDirectorySettingsService>().GetAsync(TestContext.Current.CancellationToken);

        Assert.False(settings.Enabled);
    }
}

public class DirectoryUserAdminTests
{
    private static Task<OperationResult> CreateDirectoryUserAsync(IUserAdminService service, string name, bool isAdmin = false) =>
        service.CreateAsync(new CreateUserRequest(name, string.Empty, isAdmin, true, AuthSource: AuthSources.ActiveDirectory));

    [Fact]
    public async Task CreateDirectoryUser_HasNoPassword_AndIsNeverForcedToChangeIt()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var result = await CreateDirectoryUserAsync(service, "kishi");

        Assert.True(result.Succeeded);
        var user = await users.FindByNameAsync("ad:kishi");
        Assert.NotNull(user);
        Assert.Equal("kishi", user.Email);
        Assert.True(user.IsDirectoryUser);
        Assert.Null(user.PasswordHash);
        Assert.False(user.MustChangePassword);
        Assert.Null(user.PasswordChangedAt);
        Assert.True(await users.IsInRoleAsync(user, AppRoles.User));
    }

    [Fact]
    public async Task CreateDirectoryUser_ShowsInListAndEditAsDirectory()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await CreateDirectoryUserAsync(service, "kishi", isAdmin: true);

        var summary = Assert.Single(await service.ListAsync(TestContext.Current.CancellationToken), u => u.Email == "kishi");
        var edit = await service.GetAsync(summary.Id, TestContext.Current.CancellationToken);

        Assert.Equal(AuthSources.ActiveDirectory, summary.AuthSource);
        Assert.True(summary.IsAdmin);
        Assert.Equal(AuthSources.ActiveDirectory, edit!.AuthSource);
    }

    [Fact]
    public async Task CreateDirectoryUser_JoinsTheDefaultGroup()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await CreateDirectoryUserAsync(service, "kishi");

        var id = (await service.ListAsync(TestContext.Current.CancellationToken)).Single(u => u.Email == "kishi").Id;
        Assert.Single(db.UserGroups.Where(g => g.UserId == id));
    }

    [Fact]
    public async Task CreateDirectoryUser_Duplicate_IsRejected()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await CreateDirectoryUserAsync(service, "kishi");

        var again = await CreateDirectoryUserAsync(service, "KISHI");

        Assert.False(again.Succeeded);
    }

    [Theory]
    [InlineData("kishi@corp.test")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task CreateDirectoryUser_InvalidUserName_IsRejected(string name)
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();

        var result = await CreateDirectoryUserAsync(scope.ServiceProvider.GetRequiredService<IUserAdminService>(), name);

        Assert.Equal("Error.User.AdUserNameInvalid", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public async Task CreateLocalUser_WithoutAtSign_IsRejected()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IUserAdminService>()
            .CreateAsync(new CreateUserRequest("kishi", "Passw0rd!", false, false));

        Assert.Equal("Error.User.EmailInvalid", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public async Task CreateUser_UnknownAuthSource_IsRejected()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IUserAdminService>()
            .CreateAsync(new CreateUserRequest("a@example.com", "Passw0rd!", false, false, AuthSource: "Kerberos"));

        Assert.Equal("Error.User.AuthSourceInvalid", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public async Task CreateLocalUser_StillWorks_AndIsLocal()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();

        var result = await service.CreateAsync(new CreateUserRequest("a@example.com", "Passw0rd!", false, true));

        Assert.True(result.Succeeded);
        var user = Assert.Single(await service.ListAsync(TestContext.Current.CancellationToken), u => u.Email == "a@example.com");
        Assert.Equal(AuthSources.Local, user.AuthSource);
        Assert.True(user.MustChangePassword);
    }

    [Fact]
    public async Task CreateLocalUser_DuplicateEmail_IsStillRejected()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await service.CreateAsync(new CreateUserRequest("a@example.com", "Passw0rd!", false, false));

        var again = await service.CreateAsync(new CreateUserRequest("A@example.com", "Passw0rd!", false, false));

        Assert.False(again.Succeeded);
    }

    [Fact]
    public async Task ChangePassword_ForDirectoryUser_IsRefused()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await CreateDirectoryUserAsync(service, "kishi");
        var id = (await service.ListAsync(TestContext.Current.CancellationToken)).Single(u => u.Email == "kishi").Id;

        var result = await service.ChangePasswordAsync(id, "x", "Passw0rd!");

        Assert.Equal("Error.User.PasswordManagedByDirectory", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public async Task DirectoryUser_CannotBeSignedInWithAnyLocalPassword()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        await CreateDirectoryUserAsync(service, "kishi");
        var user = (await users.FindByNameAsync("ad:kishi"))!;

        Assert.False(await users.CheckPasswordAsync(user, string.Empty));
        Assert.False(await users.CheckPasswordAsync(user, "Passw0rd!"));
    }

    [Fact]
    public async Task DeleteDirectoryUser_Works()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        await CreateDirectoryUserAsync(service, "kishi");
        var users = await service.ListAsync(TestContext.Current.CancellationToken);
        var id = users.Single(u => u.Email == "kishi").Id;
        var adminId = users.Single(u => u.Email == DataSeeder.AdminEmail).Id;

        var result = await service.DeleteAsync(id, adminId, new DateOnly(2026, 10, 12), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(await service.GetAsync(id, TestContext.Current.CancellationToken));
    }
}

public class LdapDirectoryAuthenticatorTests
{
    private static readonly ActiveDirectorySettings Configured = new(true, "127.0.0.1", 1, DirectorySecurity.None, "corp.test");

    private static LdapDirectoryAuthenticator Create() => new(NullLogger<LdapDirectoryAuthenticator>.Instance);

    [Fact]
    public async Task EmptyPassword_IsRejectedWithoutConnecting()
    {
        // 空のパスワードの単純バインドは匿名バインドとして成功してしまうため、接続の前に拒否する。
        var status = await Create().AuthenticateAsync(Configured, "kishi", string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryAuthStatus.InvalidCredentials, status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a,b")]
    [InlineData("kishi@corp.test")]
    public async Task InvalidUserName_IsRejectedWithoutConnecting(string userName)
    {
        var status = await Create().AuthenticateAsync(Configured, userName, "Passw0rd!", TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryAuthStatus.InvalidCredentials, status);
    }

    [Fact]
    public async Task NotConfigured_IsUnavailable()
    {
        var status = await Create().AuthenticateAsync(
            ActiveDirectorySettings.Default, "kishi", "Passw0rd!", TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryAuthStatus.Unavailable, status);
    }

    [Fact]
    public async Task UnreachableServer_IsUnavailable_NotInvalidCredentials()
    {
        // 接続できないときに「パスワード違い」と誤判定すると、ロックアウトを誤って進めてしまう。
        var status = await Create().AuthenticateAsync(Configured, "kishi", "Passw0rd!", TestContext.Current.CancellationToken);

        Assert.Equal(DirectoryAuthStatus.Unavailable, status);
    }
}
