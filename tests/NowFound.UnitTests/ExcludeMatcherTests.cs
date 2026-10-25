using NowFound.Application.Indexing;
using NowFound.Application.Settings;

namespace NowFound.UnitTests;

public class ExcludeMatcherTests
{
    [Theory]
    [InlineData("*.tmp", "a.tmp", true)]
    [InlineData("*.tmp", "A.TMP", true)]
    [InlineData("*.tmp", "a.txt", false)]
    [InlineData("~$*", "~$doc.docx", true)]
    [InlineData("node_modules", "node_modules", true)]
    [InlineData("node_modules", "my_node_modules", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "abbc", false)]
    [InlineData("a.b", "aXb", false)]
    public void IsExcluded_MatchesWildcards(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, new ExcludeMatcher([pattern]).IsExcluded(name));
    }

    [Fact]
    public void IsExcluded_NoPatternsOrBlank_NeverMatches()
    {
        Assert.False(new ExcludeMatcher([]).IsExcluded("x"));
        Assert.False(new ExcludeMatcher(["", "  "]).IsExcluded("x"));
    }

    [Theory]
    [InlineData("__MACOSX/", "__MACOSX", true, true)]
    [InlineData("__MACOSX/", "__MACOSX", false, false)]     // 同名のファイルは対象外
    [InlineData("__MACOSX/", "__macosx", true, true)]
    [InlineData("$RECYCLE.BIN/", "$RECYCLE.BIN", true, true)]
    [InlineData("System Volume Information/", "System Volume Information", true, true)]
    [InlineData("build\\", "build", true, true)]
    [InlineData("node_modules", "node_modules", true, true)]  // 末尾 / なしはどちらにも一致
    [InlineData("node_modules", "node_modules", false, true)]
    public void IsExcluded_TrailingSlashMeansDirectoriesOnly(string pattern, string name, bool isDirectory, bool expected) =>
        Assert.Equal(expected, new ExcludeMatcher([pattern]).IsExcluded(name, isDirectory));

    [Theory]
    [InlineData(".DS_Store", true)]
    [InlineData("._photo.jpg", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData("desktop.ini", true)]
    [InlineData("~$report.docx", true)]
    [InlineData(".~lock.report.odt#", true)]
    [InlineData("notes.txt.swp", true)]
    [InlineData(".notes.txt.swo", true)]
    [InlineData("#draft#", true)]
    [InlineData(".#draft", true)]
    [InlineData(".nfs0000abcd", true)]
    [InlineData(".smbdelete1234", true)]
    [InlineData("report.docx", false)]
    [InlineData("notes.txt", false)]
    [InlineData("photo.jpg", false)]
    [InlineData("swap.txt", false)]
    public void BuiltInDefaults_ExcludeJunkFilesButNotNormalOnes(string name, bool expected) =>
        Assert.Equal(expected, new ExcludeMatcher(ExcludePatternRules.BuiltIn).IsExcluded(name));

    [Theory]
    [InlineData("__MACOSX")]
    [InlineData(".Spotlight-V100")]
    [InlineData(".Trashes")]
    [InlineData(".fseventsd")]
    [InlineData(".TemporaryItems")]
    [InlineData("$RECYCLE.BIN")]
    [InlineData("System Volume Information")]
    public void BuiltInDefaults_ExcludeSystemFoldersOnlyAsDirectories(string name)
    {
        var matcher = new ExcludeMatcher(ExcludePatternRules.BuiltIn);
        Assert.True(matcher.IsExcluded(name, isDirectory: true));
        Assert.False(matcher.IsExcluded(name, isDirectory: false));
    }
}
