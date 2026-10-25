using Microsoft.Extensions.DependencyInjection;
using NowFound.Application;
using NowFound.Application.Logging;
using NowFound.Application.Search;
using NowFound.Domain.Entities;
using NowFound.Infrastructure.Data;
using NowFound.Infrastructure.Logging;
using NowFound.Web.Services;

namespace NowFound.UnitTests;

public class OperationLogServiceTests
{
    private static OperationLogEntry Entry(OperationType type, string email, LocalizedMessage? detail = null) =>
        new(type, null, email, detail, "127.0.0.1");

    private static async Task SeedAsync(IServiceProvider sp, params (DateTime At, OperationLogEntry Entry)[] rows)
    {
        var db = sp.GetRequiredService<OperationLogDbContext>();
        foreach (var (at, entry) in rows)
        {
            db.OperationLogs.Add(new OperationLog
            {
                Timestamp = at,
                Operation = entry.Operation,
                UserEmail = entry.UserEmail,
                DetailKey = entry.Detail?.Key,
                DetailArgs = entry.Detail is { Args.Length: > 0 } d ? System.Text.Json.JsonSerializer.Serialize(d.FormatArgs()) : null,
                IpAddress = entry.IpAddress,
            });
        }

        await db.SaveChangesAsync();
    }

    private static OperationLogQuery Query(
        DateTime? from = null, DateTime? to = null, OperationType? op = null, string? user = null, int page = 1, int size = 50) =>
        new(from, to, op, user, page, size);

    [Fact]
    public async Task Write_StoresEntryWithUtcTimestamp()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOperationLogService>();
        var before = DateTime.UtcNow.AddSeconds(-1);

        await service.WriteAsync(Entry(OperationType.Login, "a@example.com", new LocalizedMessage("Log.Test", "x", 5)), TestContext.Current.CancellationToken);
        var page = await service.SearchAsync(Query(), TestContext.Current.CancellationToken);

        var item = Assert.Single(page.Items);
        Assert.Equal(OperationType.Login, item.Operation);
        Assert.Equal("a@example.com", item.UserEmail);
        Assert.Equal("Log.Test", item.DetailKey);
        Assert.Equal(["x", "5"], item.DetailArgs);
        Assert.True(item.HasDetailKey);
        Assert.True(item.Timestamp >= before);
    }

    [Fact]
    public async Task Write_WithoutDetail_StoresNoKey_AndLegacyRowsKeepTheirText()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOperationLogService>();
        var db = scope.ServiceProvider.GetRequiredService<OperationLogDbContext>();

        await service.WriteAsync(new OperationLogEntry(OperationType.Logout, null, "a@example.com", null, null), TestContext.Current.CancellationToken);
        db.OperationLogs.Add(new OperationLog { Timestamp = DateTime.UtcNow, Operation = OperationType.Search, UserEmail = "b@example.com", Detail = "旧形式の文章" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var items = (await service.SearchAsync(Query(), TestContext.Current.CancellationToken)).Items;
        var modern = items.Single(i => i.Operation == OperationType.Logout);
        var legacy = items.Single(i => i.Operation == OperationType.Search);
        Assert.False(modern.HasDetailKey);
        Assert.Empty(modern.DetailArgs!);
        Assert.False(legacy.HasDetailKey);
        Assert.Equal("旧形式の文章", legacy.Detail);
    }

    [Fact]
    public async Task Write_TruncatesVeryLongDetail_SafelyWithoutLosingArguments()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOperationLogService>();

        await service.WriteAsync(Entry(OperationType.Search, "a@example.com", new LocalizedMessage("Log.Test", new string('x', 5000))), TestContext.Current.CancellationToken);

        // 長すぎる引数はシリアライズ前に安全に切り詰め、有効な JSON 配列として保存する。
        // （以前のように JSON 破損による構文エラーで引数が全件消失することはない）
        var item = Assert.Single((await service.SearchAsync(Query(), TestContext.Current.CancellationToken)).Items);
        Assert.Equal("Log.Test", item.DetailKey);
        var arg = Assert.Single(item.DetailArgs!);
        Assert.StartsWith("xxx", arg);
        Assert.EndsWith("...", arg);
        Assert.True(arg.Length <= OperationLogService.MaxSingleArgLength + 3);
    }

    [Fact]
    public async Task Search_FiltersByOperationUserAndDateAndOrdersNewestFirst()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var day = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
        await SeedAsync(
            sp,
            (day.AddDays(-5), Entry(OperationType.Login, "old@example.com")),
            (day, Entry(OperationType.Login, "alice@example.com")),
            (day.AddHours(1), Entry(OperationType.Search, "alice@example.com", "q")),
            (day.AddDays(2), Entry(OperationType.Logout, "bob_x@example.com")));
        var service = sp.GetRequiredService<IOperationLogService>();

        var byOp = await service.SearchAsync(Query(op: OperationType.Login), TestContext.Current.CancellationToken);
        var byUser = await service.SearchAsync(Query(user: "ALICE"), TestContext.Current.CancellationToken);
        var byDay = await service.SearchAsync(Query(from: day.ToLocalTime().Date, to: day.ToLocalTime().Date), TestContext.Current.CancellationToken);
        var literalUnderscore = await service.SearchAsync(Query(user: "_x"), TestContext.Current.CancellationToken);
        var wildcard = await service.SearchAsync(Query(user: "%"), TestContext.Current.CancellationToken);

        Assert.Equal(2, byOp.TotalCount);
        Assert.Equal(["Search", "Login"], byUser.Items.Select(i => i.Operation.ToString()));
        Assert.Equal(2, byDay.TotalCount);
        Assert.Single(literalUnderscore.Items);
        Assert.Empty(wildcard.Items);
    }

    [Fact]
    public async Task Search_Pages()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedAsync(sp, [.. Enumerable.Range(0, 5).Select(i =>
            (DateTime.UtcNow.AddMinutes(-i), Entry(OperationType.Login, $"u{i}@example.com")))]);
        var service = sp.GetRequiredService<IOperationLogService>();

        var second = await service.SearchAsync(Query(page: 2, size: 2), TestContext.Current.CancellationToken);
        var last = await service.SearchAsync(Query(page: 3, size: 2), TestContext.Current.CancellationToken);

        Assert.Equal(5, second.TotalCount);
        Assert.Equal(3, second.TotalPages);
        Assert.Equal(["u2@example.com", "u3@example.com"], second.Items.Select(i => i.UserEmail));
        Assert.Single(last.Items);
        Assert.Equal(1, (await service.SearchAsync(Query(page: 0), TestContext.Current.CancellationToken)).Page);
    }

    [Fact]
    public void SearchDescriber_FormatsConditionsAndFolder()
    {
        var (text, empty) = SearchLocalizerTestHelper.Japanese(localizer => (
            SearchDescriber.Describe(
                [
                    new SearchCondition { Field = SearchField.FileName, Text = " abc " },
                    new SearchCondition { Field = SearchField.ModifiedDate, From = new DateTime(2026, 1, 2), To = new DateTime(2026, 1, 5) },
                    new SearchCondition { Field = SearchField.Extension, Text = " " },
                ],
                SearchOperator.Or,
                "Main",
                "C:\\data\\sub",
                localizer),
            SearchDescriber.Describe([], SearchOperator.And, null, null, localizer)));

        Assert.Equal("FileName='abc' OR ModifiedDate=2026-01-02～2026-01-05 | フォルダ=Main (C:\\data\\sub)", text);
        Assert.Equal("(条件なし)", empty);
    }

    [Fact]
    public async Task Search_WithExplicitTimeZone_FiltersByLocalDayBoundaries()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

        // UTC 2026-10-13 16:00:00 => JST 2026-10-14 01:00:00
        var earlyJst = new DateTime(2026, 10, 13, 16, 0, 0, DateTimeKind.Utc);
        // UTC 2026-10-14 14:00:00 => JST 2026-10-14 23:00:00
        var lateJst = new DateTime(2026, 10, 14, 14, 0, 0, DateTimeKind.Utc);
        // UTC 2026-10-14 16:00:00 => JST 2026-10-15 01:00:00 (翌日)
        var nextDayJst = new DateTime(2026, 10, 14, 16, 0, 0, DateTimeKind.Utc);

        await SeedAsync(
            sp,
            (earlyJst, Entry(OperationType.Login, "early@example.com")),
            (lateJst, Entry(OperationType.Login, "late@example.com")),
            (nextDayJst, Entry(OperationType.Login, "next@example.com")));

        var service = sp.GetRequiredService<IOperationLogService>();

        // JST の 2026-10-14 を指定して検索
        var query = new OperationLogQuery(new DateTime(2026, 10, 14), new DateTime(2026, 10, 14), null, null, 1, 50, TimeZone: jst);
        var page = await service.SearchAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Items, i => i.UserEmail == "early@example.com");
        Assert.Contains(page.Items, i => i.UserEmail == "late@example.com");
        Assert.DoesNotContain(page.Items, i => i.UserEmail == "next@example.com");
    }

    [Fact]
    public void SerializeArgs_WhenSingleArgIsExtremelyLong_PreservesValidJsonAndTruncatesArg()
    {
        var longString = new string('A', 3000);
        var json = OperationLogService.SerializeArgs([longString]);

        Assert.True(json.Length <= OperationLogService.MaxDetailLength);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
        Assert.NotNull(parsed);
        var single = Assert.Single(parsed);
        Assert.StartsWith(new string('A', 100), single);
        Assert.EndsWith("...", single);
    }

    [Fact]
    public void SerializeArgs_WhenTotalLengthExceedsLimit_PreservesValidJsonArray()
    {
        var manyArgs = Enumerable.Range(0, 50).Select(i => $"arg-{i}: {new string('x', 100)}").ToArray();
        var json = OperationLogService.SerializeArgs(manyArgs);

        Assert.True(json.Length <= OperationLogService.MaxDetailLength);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
        Assert.NotNull(parsed);
        Assert.NotEmpty(parsed);
        Assert.Equal("...", parsed[^1]);
    }

    [Fact]
    public async Task Write_WithExtremelyLongDetailArgs_PreservesParsableArgumentsWithoutDataLoss()
    {
        await using var host = await TestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOperationLogService>();

        var longArg1 = new string('x', 1500);
        var longArg2 = new string('y', 1500);
        var entry = Entry(OperationType.Search, "user@example.com", new LocalizedMessage("Log.Search", longArg1, longArg2));

        await service.WriteAsync(entry, TestContext.Current.CancellationToken);

        var page = await service.SearchAsync(Query(), TestContext.Current.CancellationToken);
        var item = Assert.Single(page.Items);

        // 以前の実装では JSON 切断により JsonException が発生し空配列 [] になっていた
        Assert.NotNull(item.DetailArgs);
        Assert.NotEmpty(item.DetailArgs);
        Assert.True(item.DetailArgs.Count >= 1);
        Assert.StartsWith("xxx", item.DetailArgs[0]);
    }
}

