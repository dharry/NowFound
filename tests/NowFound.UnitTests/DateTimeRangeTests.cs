using NowFound.Application;

namespace NowFound.UnitTests;

public class DateTimeRangeTests
{
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    [Fact]
    public void ToUtc_WithJst_ConvertsDateRangeCorrectly()
    {
        var from = new DateTime(2026, 10, 14, 10, 30, 0); // 時刻が含まれていても 00:00:00 JST として解釈される
        var to = new DateTime(2026, 10, 14, 23, 59, 59);   // 翌日 00:00:00 JST 未満として解釈される

        var (fromUtc, toUtc) = DateTimeRange.ToUtc(from, to, Jst);

        // 2026-10-14 00:00:00 JST は 2026-10-13 15:00:00 UTC
        Assert.Equal(new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Utc), fromUtc);
        // 2026-10-15 00:00:00 JST は 2026-10-14 15:00:00 UTC
        Assert.Equal(new DateTime(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc), toUtc);
    }

    [Fact]
    public void ToUtc_WithUtc_ConvertsDateRangeCorrectly()
    {
        var from = new DateTime(2026, 10, 14);
        var to = new DateTime(2026, 10, 15);

        var (fromUtc, toUtc) = DateTimeRange.ToUtc(from, to, TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc), fromUtc);
        Assert.Equal(new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc), toUtc);
    }

    [Fact]
    public void ToUtc_WhenNull_ReturnsNull()
    {
        var (fromUtc, toUtc) = DateTimeRange.ToUtc(null, null, Jst);

        Assert.Null(fromUtc);
        Assert.Null(toUtc);
    }

    [Fact]
    public void ToUtc_WhenFromOnly_ReturnsFromUtcAndNullTo()
    {
        var from = new DateTime(2026, 5, 1);
        var (fromUtc, toUtc) = DateTimeRange.ToUtc(from, null, Jst);

        Assert.Equal(new DateTime(2026, 4, 30, 15, 0, 0, DateTimeKind.Utc), fromUtc);
        Assert.Null(toUtc);
    }

    [Fact]
    public void ToUtc_WhenToOnly_ReturnsNullFromAndToUtc()
    {
        var to = new DateTime(2026, 5, 1);
        var (fromUtc, toUtc) = DateTimeRange.ToUtc(null, to, Jst);

        Assert.Null(fromUtc);
        Assert.Equal(new DateTime(2026, 5, 1, 15, 0, 0, DateTimeKind.Utc), toUtc);
    }

    [Fact]
    public void ToUtc_WithoutTimeZone_UsesLocal()
    {
        var from = new DateTime(2026, 1, 1);
        var (fromUtc, _) = DateTimeRange.ToUtc(from, null);

        var expectedUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(from.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        Assert.Equal(expectedUtc, fromUtc);
    }
}
