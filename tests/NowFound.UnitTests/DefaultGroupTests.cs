using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

public sealed class DefaultGroupTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private IServiceScope _scope = null!;
    private IAdminSettingsService _settings = null!;
    private IUserAdminService _users = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _scope = _host.Services.CreateScope();
        _settings = _scope.ServiceProvider.GetRequiredService<IAdminSettingsService>();
        _users = _scope.ServiceProvider.GetRequiredService<IUserAdminService>();
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<GroupSummary> DefaultGroupAsync() =>
        (await _settings.ListGroupsAsync(TestContext.Current.CancellationToken)).Single(g => g.IsDefault);

    [Fact]
    public async Task Seed_CreatesTheDefaultGroup_WithExistingUsersAsMembers()
    {
        var group = await DefaultGroupAsync();

        Assert.Equal("デフォルトグループ", group.Name);
        Assert.Equal(1, group.UserCount); // 初期管理者
    }

    [Fact]
    public async Task Seed_IsIdempotent()
    {
        var db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await DataSeeder.SeedDefaultGroupAsync(db, TestContext.Current.CancellationToken);

        Assert.Single(await _settings.ListGroupsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Seed_PromotesAnExistingGroupWithTheSameName_InsteadOfDuplicating()
    {
        var db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.Groups.SingleAsync(TestContext.Current.CancellationToken);
        existing.IsDefault = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await DataSeeder.SeedDefaultGroupAsync(db, TestContext.Current.CancellationToken);

        var group = await db.Groups.SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(group.IsDefault);
        Assert.Equal(existing.Id, group.Id);
    }

    [Fact]
    public async Task NewUsers_JoinTheDefaultGroupAutomatically()
    {
        var before = (await DefaultGroupAsync()).UserCount;

        await _users.CreateAsync(new CreateUserRequest("new@example.com", "Passw0rd!", false, false));

        Assert.Equal(before + 1, (await DefaultGroupAsync()).UserCount);
    }

    [Fact]
    public async Task Delete_IsRefusedForTheDefaultGroup_ButAllowedForOthers()
    {
        var defaultGroup = await DefaultGroupAsync();
        await _settings.SaveGroupAsync(new GroupEdit { Name = "Other" }, TestContext.Current.CancellationToken);
        var other = (await _settings.ListGroupsAsync(TestContext.Current.CancellationToken)).Single(g => !g.IsDefault);

        var refused = await _settings.DeleteGroupAsync(defaultGroup.Id, TestContext.Current.CancellationToken);
        var allowed = await _settings.DeleteGroupAsync(other.Id, TestContext.Current.CancellationToken);

        Assert.False(refused.Succeeded);
        Assert.True(allowed.Succeeded);
        Assert.Equal([defaultGroup.Id], (await _settings.ListGroupsAsync(TestContext.Current.CancellationToken)).Select(g => g.Id));
    }

    [Fact]
    public async Task Save_KeepsTheDefaultGroupName_ButAllowsEditingMembersAndFolders()
    {
        await _settings.SaveFolderAsync(new FolderEdit { Name = "F", Path = Path.GetTempPath() }, TestContext.Current.CancellationToken);
        var folderId = (await _settings.ListFoldersAsync(TestContext.Current.CancellationToken)).Single().Id;
        var group = await DefaultGroupAsync();

        var result = await _settings.SaveGroupAsync(
            new GroupEdit { Id = group.Id, Name = "改名", Description = "説明", FolderIds = [folderId] },
            TestContext.Current.CancellationToken);

        var saved = await _settings.GetGroupAsync(group.Id, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal("デフォルトグループ", saved!.Name);
        Assert.True(saved.IsDefault);
        Assert.Equal("説明", saved.Description);
        Assert.Equal([folderId], saved.FolderIds);
    }
}
