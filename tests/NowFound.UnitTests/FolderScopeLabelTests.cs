using NowFound.Application.Search;
using NowFound.Web.Services;

namespace NowFound.UnitTests;

public class FolderScopeLabelTests
{
    private static readonly AccessibleFolder Root = new(1, "人事部", @"D:\NAS\人事部");

    [Fact]
    public void Build_WithoutASubfolder_ReturnsTheDisplayName() =>
        Assert.Equal("人事部", FolderScopeLabel.Build(Root, null));

    [Fact]
    public void Build_WithTheRootPath_ReturnsTheDisplayName() =>
        Assert.Equal("人事部", FolderScopeLabel.Build(Root, @"D:\NAS\人事部\"));

    [Theory]
    [InlineData(@"D:\NAS\人事部\2024", @"人事部\2024")]
    [InlineData(@"D:\NAS\人事部\2024\給与\", @"人事部\2024\給与")]
    [InlineData(@"d:\nas\人事部\2024", @"人事部\2024")]
    [InlineData("D:/NAS/人事部/2024/給与", @"人事部\2024\給与")]
    public void Build_ReplacesTheRootWithTheDisplayName(string path, string expected) =>
        Assert.Equal(expected, FolderScopeLabel.Build(Root, path));

    [Fact]
    public void Build_DoesNotTreatASiblingWithTheSamePrefixAsASubfolder() =>
        Assert.Equal(@"D:\NAS\人事部2", FolderScopeLabel.Build(Root, @"D:\NAS\人事部2"));

    [Fact]
    public void Build_WithoutARoot_FallsBackToThePath()
    {
        Assert.Equal(@"D:\x", FolderScopeLabel.Build(null, @"D:\x"));
        Assert.Null(FolderScopeLabel.Build(null, null));
    }
}
