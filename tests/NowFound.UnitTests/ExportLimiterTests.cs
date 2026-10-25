using NowFound.Web.Services;

namespace NowFound.UnitTests;

public class ExportLimiterTests
{
    [Fact]
    public void TryEnter_RefusesBeyondTheLimit_AndReleasesOnDispose()
    {
        var limiter = new ExportLimiter(2);

        var first = limiter.TryEnter();
        var second = limiter.TryEnter();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(limiter.TryEnter());

        first.Dispose();
        using var third = limiter.TryEnter();
        Assert.NotNull(third);
        Assert.Null(limiter.TryEnter());
        second.Dispose();
    }

    [Fact]
    public void Dispose_Twice_ReleasesOnlyOnce()
    {
        var limiter = new ExportLimiter(1);
        var lease = limiter.TryEnter()!;

        lease.Dispose();
        lease.Dispose();

        using var again = limiter.TryEnter();
        Assert.NotNull(again);
        Assert.Null(limiter.TryEnter());
    }

    [Fact]
    public void DefaultLimit_IsTwo()
    {
        var limiter = new ExportLimiter();

        using var a = limiter.TryEnter();
        using var b = limiter.TryEnter();

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(limiter.TryEnter());
    }
}
