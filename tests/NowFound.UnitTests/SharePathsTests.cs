using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Search;
using NowFound.Application.Settings;
using NowFound.Application.Users;
using NowFound.Infrastructure.Data;

namespace NowFound.UnitTests;

public class SharePathsTests
{
    [Theory]
    [InlineData(@"D:\MyShare", @"\\PC01\MyShare")]
    [InlineData(@"D:\MyShare\", @"\\PC01\MyShare")]
    [InlineData(@"D:\MyShare\sub\a.txt", @"\\PC01\MyShare\sub\a.txt")]
    [InlineData(@"d:\myshare\Sub", @"\\PC01\MyShare\Sub")]
    [InlineData(@"D:\MyShareOther\a.txt", @"D:\MyShareOther\a.txt")]
    [InlineData(@"E:\elsewhere", @"E:\elsewhere")]
    public void ToDisplayPath_ReplacesTheRootWithUnc_OnlyUnderTheRoot(string path, string expected) =>
        Assert.Equal(expected, SharePaths.ToDisplayPath(path, @"D:\MyShare", true, "MyShare", "PC01"));

    [Theory]
    [InlineData(false, "MyShare")]
    [InlineData(true, null)]
    [InlineData(true, " ")]
    public void ToDisplayPath_ReturnsThePathUnchanged_WhenNotShared(bool isShared, string? shareName) =>
        Assert.Equal(@"D:\MyShare\a.txt", SharePaths.ToDisplayPath(@"D:\MyShare\a.txt", @"D:\MyShare", isShared, shareName, "PC01"));

    [Fact]
    public void ToDisplayPath_UsesTheGivenShareName_NotTheFolderName() =>
        Assert.Equal(@"\\PC01\Docs\a.txt", SharePaths.ToDisplayPath(@"D:\data\a.txt", @"D:\data", true, "Docs", "PC01"));

    [Theory]
    [InlineData(@"D:\MyShare", "表示名", "MyShare")]
    [InlineData(@"D:\MyShare\", "表示名", "MyShare")]
    [InlineData(@"D:\", "表示名", "表示名")]
    public void DefaultShareName_IsTheFolderName_FallingBackToTheDisplayName(string path, string display, string expected) =>
        Assert.Equal(expected, SharePaths.DefaultShareName(path, display));

    [Theory]
    [InlineData("MyShare", true)]
    [InlineData("共有$", true)]
    [InlineData("", false)]
    [InlineData(@"a\b", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    [InlineData("a*b", false)]
    public void IsValidShareName_RejectsEmptyAndReservedCharacters(string name, bool expected) =>
        Assert.Equal(expected, SharePaths.IsValidShareName(name));
}

public sealed class SharedFolderServiceTests : IAsyncLifetime
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mk-share"));

    private TestHost _host = null!;
    private IServiceScope _scope = null!;
    private IAdminSettingsService _settings = null!;
    private ISearchService _search = null!;
    private string _adminId = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _scope = _host.Services.CreateScope();
        var sp = _scope.ServiceProvider;
        _settings = sp.GetRequiredService<IAdminSettingsService>();
        _search = sp.GetRequiredService<ISearchService>();
        _adminId = (await sp.GetRequiredService<IUserAdminService>().ListAsync()).Single(u => u.IsAdmin).Id;
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Save_DefaultsTheShareNameToTheFolderName_WhenLeftBlank()
    {
        var result = await _settings.SaveFolderAsync(new FolderEdit { Name = "共有", Path = Root, IsShared = true }, TestContext.Current.CancellationToken);
        var saved = await _settings.GetFolderAsync((await _settings.ListFoldersAsync(TestContext.Current.CancellationToken)).Single().Id, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(saved!.IsShared);
        Assert.Equal("mk-share", saved.ShareName);
    }

    [Fact]
    public async Task Save_KeepsAnExplicitShareName_AndClearsItWhenNoLongerShared()
    {
        await _settings.SaveFolderAsync(new FolderEdit { Name = "共有", Path = Root, IsShared = true, ShareName = " Docs " }, TestContext.Current.CancellationToken);
        var id = (await _settings.ListFoldersAsync(TestContext.Current.CancellationToken)).Single().Id;
        Assert.Equal("Docs", (await _settings.GetFolderAsync(id, TestContext.Current.CancellationToken))!.ShareName);

        await _settings.SaveFolderAsync(new FolderEdit { Id = id, Name = "共有", Path = Root, IsShared = false, ShareName = "Docs" }, TestContext.Current.CancellationToken);
        var saved = await _settings.GetFolderAsync(id, TestContext.Current.CancellationToken);
        Assert.False(saved!.IsShared);
        Assert.Null(saved.ShareName);
    }

    [Fact]
    public async Task Save_RejectsAShareNameWithReservedCharacters()
    {
        var result = await _settings.SaveFolderAsync(new FolderEdit { Name = "共有", Path = Root, IsShared = true, ShareName = @"a\b" }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(await _settings.ListFoldersAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Search_ReturnsUncPathsForCopyAndDisplay_ButKeepsTheLocalFolderPathForFiltering()
    {
        await _settings.SaveFolderAsync(new FolderEdit { Name = "共有", Path = Root, IsShared = true, ShareName = "Docs" }, TestContext.Current.CancellationToken);
        var id = (await _settings.ListFoldersAsync(TestContext.Current.CancellationToken)).Single().Id;
        await new FilesSeeder(_scope.ServiceProvider.GetRequiredService<FilesDbContext>()).AddAsync(id, Root, "a.txt", "txt", 1);

        var page = await _search.SearchAsync(new SearchRequest(_adminId, true, [], SearchOperator.And, id, null, 1), TestContext.Current.CancellationToken);
        var item = page.Items.Single();

        Assert.Equal($@"\\{Environment.MachineName}\Docs\a.txt", item.CopyPath);
        Assert.Equal($@"\\{Environment.MachineName}\Docs", item.ShownFolderPath);
        Assert.Equal(Root, item.FolderPath);
        Assert.Equal(Path.Combine(Root, "a.txt"), item.FullPath);
    }

    [Fact]
    public async Task Search_KeepsLocalPaths_WhenTheFolderIsNotShared()
    {
        await _settings.SaveFolderAsync(new FolderEdit { Name = "通常", Path = Root }, TestContext.Current.CancellationToken);
        var id = (await _settings.ListFoldersAsync(TestContext.Current.CancellationToken)).Single().Id;
        await new FilesSeeder(_scope.ServiceProvider.GetRequiredService<FilesDbContext>()).AddAsync(id, Root, "a.txt", "txt", 1);

        var item = (await _search.SearchAsync(new SearchRequest(_adminId, true, [], SearchOperator.And, id, null, 1), TestContext.Current.CancellationToken)).Items.Single();

        Assert.Equal(Path.Combine(Root, "a.txt"), item.CopyPath);
        Assert.Equal(Root, item.ShownFolderPath);
    }
}
