using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NowFound.Application.Settings;

namespace NowFound.IntegrationTests;

/// <summary>
/// 検索条件入力中のフォルダ絞り込み変更・解除時に、入力値が保持されることの検証。
/// </summary>
public sealed class FolderSearchRetentionTests : IDisposable
{
    private readonly WebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task FolderLinks_RetainConditions_InHrefAndDataAttributes()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var rootA = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NowFoundTestA_" + Guid.NewGuid()));
        var rootB = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NowFoundTestB_" + Guid.NewGuid()));
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);

        try
        {
            await admin.PostFormAsync("/Folders/Save", "/Folders/Create",
            [
                new("Name", "FolderA"),
                new("Path", rootA),
            ]);
            await admin.PostFormAsync("/Folders/Save", "/Folders/Create",
            [
                new("Name", "FolderB"),
                new("Path", rootB),
            ]);

            using var scope = _factory.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IAdminSettingsService>();
            var folders = await settings.ListFoldersAsync(TestContext.Current.CancellationToken);
            var folderA = folders.Single(f => f.Name == "FolderA");
            var folderB = folders.Single(f => f.Name == "FolderB");

            // FolderA で絞り込んだ状態で、キーワード "sample-keyword" を指定して検索画面を開く
            var html = await admin.GetStringAsync(
                $"/?FolderId={folderA.Id}&Conditions[0].Field=FileName&Conditions[0].Text=sample-keyword",
                TestContext.Current.CancellationToken);

            // 1. 検索入力ボックスにキーワードが保持されていること
            Assert.Contains("value=\"sample-keyword\"", html);

            // 2. 絞り込み解除リンクに data-clear-folder="true" が付与され、href にキーワードが保持されていること
            Assert.Contains("data-clear-folder=\"true\"", html);
            Assert.Matches(@"href=""/[^""]*Conditions(%5B|\[)0(%5D|\])\.Text=sample-keyword[^""]*""[^>]*data-clear-folder=""true""", html);

            // 3. 「すべてのフォルダー」リンクにも data-clear-folder="true" が付与され、href にキーワードが保持されていること
            Assert.Matches(@"<li><a\s+href=""/[^""]*Conditions(%5B|\[)0(%5D|\])\.Text=sample-keyword[^""]*""[^>]*data-clear-folder=""true""", html);

            // 4. FolderB のリンクに data-folder-id が付与され、href にキーワードおよび FolderId が保持されていること
            Assert.Contains($"data-folder-id=\"{folderB.Id}\"", html);
            Assert.Matches($@"href=""/[^""]*Conditions(%5B|\[)0(%5D|\])\.Text=sample-keyword[^""]*""[^>]*data-folder-id=""{folderB.Id}""", html);
        }
        finally
        {
            if (Directory.Exists(rootA)) Directory.Delete(rootA, true);
            if (Directory.Exists(rootB)) Directory.Delete(rootB, true);
        }
    }

    [Fact]
    public async Task FolderChange_ViaQuery_PreservesConditionInput()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NowFoundTest_" + Guid.NewGuid()));
        Directory.CreateDirectory(root);

        try
        {
            await admin.PostFormAsync("/Folders/Save", "/Folders/Create",
            [
                new("Name", "TargetFolder"),
                new("Path", root),
            ]);

            using var scope = _factory.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IAdminSettingsService>();
            var folder = (await settings.ListFoldersAsync(TestContext.Current.CancellationToken)).Single(f => f.Name == "TargetFolder");

            // フォルダ絞り込み変更時（フォーム送信）を模したリクエスト:
            // FolderId を TargetFolder に変更しつつ、入力値 "mykeyword" を送信
            var html = await admin.GetStringAsync(
                $"/?FolderId={folder.Id}&Conditions[0].Field=FileName&Conditions[0].Text=mykeyword",
                TestContext.Current.CancellationToken);

            // 検索入力欄にキーワードが保持されていること
            Assert.Contains("value=\"mykeyword\"", html);

            // 絞り込みスコープ表示にフォルダ名が出ていること
            Assert.Contains("<strong>TargetFolder</strong>", html);

            // TargetFolder のリンクが current クラスになっていること
            Assert.Contains($"class=\"current\">TargetFolder</a>", html);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FolderClear_ViaQuery_PreservesConditionInput()
    {
        var admin = _factory.CreateBrowserLikeClient();
        await admin.LoginAsAdminAsync();

        // 「すべてのフォルダー」または「絞り込み解除」クリック時を模したリクエスト:
        // FolderId=&FolderPath= とともに、入力値 "keepme" を送信
        var html = await admin.GetStringAsync(
            "/?FolderId=&FolderPath=&Conditions[0].Field=FileName&Conditions[0].Text=keepme",
            TestContext.Current.CancellationToken);

        // 検索入力欄にキーワードが保持されていること
        Assert.Contains("value=\"keepme\"", html);

        // 絞り込みスコープ表示は出ないこと
        Assert.DoesNotContain("<p class=\"scope\">", html);

        // 「すべてのフォルダー」が current になっていること
        Assert.Matches(@"<a\s+href=""/[^""]*""\s+class=""current""\s+data-clear-folder=""true"">", html);
    }
}
