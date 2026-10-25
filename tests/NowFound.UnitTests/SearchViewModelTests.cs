using NowFound.Application.Search;
using NowFound.Web.Models;

namespace NowFound.UnitTests;

public class SearchViewModelTests
{
    private static SearchViewModel Model() => new()
    {
        Operator = SearchOperator.Or,
        FolderId = 3,
        FolderPath = "C:\\data",
        Sort = SearchSortKey.Size,
        Desc = true,
        Conditions =
        [
            new SearchCondition { Field = SearchField.FileName, Text = "abc" },
            new SearchCondition { Field = SearchField.ModifiedDate, From = new DateTime(2026, 1, 2) },
        ],
    };

    [Fact]
    public void ToRouteValues_KeepsConditionsSortAndScope()
    {
        var values = Model().ToRouteValues(4);

        Assert.Equal("4", values["Page"]);
        Assert.Equal("Or", values["Operator"]);
        Assert.Equal("Size", values["Sort"]);
        Assert.Equal("true", values["Desc"]);
        Assert.Equal("3", values["FolderId"]);
        Assert.Equal("C:\\data", values["FolderPath"]);
        Assert.Equal("FileName", values["Conditions[0].Field"]);
        Assert.Equal("abc", values["Conditions[0].Text"]);
        Assert.Equal("2026-01-02", values["Conditions[1].From"]);
    }

    [Fact]
    public void ToRouteValues_CanOverrideSortAndFolder()
    {
        var values = Model().ToRouteValues(1, SearchSortKey.Name, false, folderId: 9, folderPath: "D:\\other");

        Assert.Equal("Name", values["Sort"]);
        Assert.Equal("false", values["Desc"]);
        Assert.Equal("9", values["FolderId"]);
        Assert.Equal("D:\\other", values["FolderPath"]);
    }

    [Fact]
    public void ToRouteValues_FolderOverrideWithoutPath_DropsOldPath()
    {
        var values = Model().ToRouteValues(1, folderId: 9, folderPath: null);

        Assert.Equal("9", values["FolderId"]);
        Assert.False(values.ContainsKey("FolderPath"));
    }

    [Fact]
    public void ToRouteValues_ClearFolder_RemovesFolderIdAndFolderPath_WhileKeepingConditions()
    {
        var values = Model().ToRouteValues(1, clearFolder: true);

        Assert.False(values.ContainsKey("FolderId"));
        Assert.False(values.ContainsKey("FolderPath"));
        Assert.Equal("1", values["Page"]);
        Assert.Equal("Or", values["Operator"]);
        Assert.Equal("Size", values["Sort"]);
        Assert.Equal("true", values["Desc"]);
        Assert.Equal("FileName", values["Conditions[0].Field"]);
        Assert.Equal("abc", values["Conditions[0].Text"]);
        Assert.Equal("2026-01-02", values["Conditions[1].From"]);
    }

    [Fact]
    public void ToRouteValues_ChangeFolder_ReplacesFolderIdAndDropsPath_WhileKeepingConditions()
    {
        var values = Model().ToRouteValues(1, folderId: 42, folderPath: "");

        Assert.Equal("42", values["FolderId"]);
        Assert.False(values.ContainsKey("FolderPath"));
        Assert.Equal("1", values["Page"]);
        Assert.Equal("FileName", values["Conditions[0].Field"]);
        Assert.Equal("abc", values["Conditions[0].Text"]);
    }
}
