using NowFound.Domain.Entities;
using NowFound.Web.Models;

namespace NowFound.UnitTests;

public class LogSearchViewModelTests
{
    private static LogSearchViewModel Model() => new()
    {
        From = new DateTime(2026, 1, 2),
        Operation = OperationType.Login,
        User = "alice",
    };

    [Fact]
    public void ToRouteValues_KeepsTheCurrentConditions_ByDefault()
    {
        var values = Model().ToRouteValues(3);

        Assert.Equal("2026-01-02", values["From"]);
        Assert.Equal("Login", values["Operation"]);
        Assert.Equal("alice", values["User"]);
        Assert.Equal("3", values["Page"]);
    }

    [Fact]
    public void ToRouteValues_ReplacesOnlyTheOperation_WhenGiven()
    {
        var values = Model().ToRouteValues(1, operation: OperationType.AdminChange);

        Assert.Equal("AdminChange", values["Operation"]);
        Assert.Equal("alice", values["User"]);
        Assert.Equal("2026-01-02", values["From"]);
    }

    [Fact]
    public void ToRouteValues_ReplacesOnlyTheUser_WhenGiven()
    {
        var values = Model().ToRouteValues(1, user: "bob@example.com");

        Assert.Equal("bob@example.com", values["User"]);
        Assert.Equal("Login", values["Operation"]);
    }
}
