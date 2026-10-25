using Microsoft.Extensions.Caching.Memory;
using NowFound.Application.Users;
using NowFound.Infrastructure.Identity;

namespace NowFound.UnitTests;

public class MemoryAccountStatusCacheTests
{
    [Fact]
    public async Task GetOrLoadAsync_CachesResult_AndOnlyCallsFactoryOnce()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new MemoryAccountStatusCache(memoryCache);

        var callCount = 0;
        Task<AccountStateSnapshot> Factory(string id, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult(new AccountStateSnapshot(true, true, null, null, "stamp1"));
        }

        var result1 = await cache.GetOrLoadAsync("user1", Factory, TestContext.Current.CancellationToken);
        var result2 = await cache.GetOrLoadAsync("user1", Factory, TestContext.Current.CancellationToken);

        Assert.Equal(1, callCount);
        Assert.Equal("stamp1", result1.SecurityStamp);
        Assert.Equal("stamp1", result2.SecurityStamp);
    }

    [Fact]
    public async Task Invalidate_EvictsCacheEntry_AndCallsFactoryAgain()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new MemoryAccountStatusCache(memoryCache);

        var callCount = 0;
        Task<AccountStateSnapshot> Factory(string id, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult(new AccountStateSnapshot(true, true, null, null, $"stamp{callCount}"));
        }

        var result1 = await cache.GetOrLoadAsync("user1", Factory, TestContext.Current.CancellationToken);
        Assert.Equal(1, callCount);
        Assert.Equal("stamp1", result1.SecurityStamp);

        cache.Invalidate("user1");

        var result2 = await cache.GetOrLoadAsync("user1", Factory, TestContext.Current.CancellationToken);
        Assert.Equal(2, callCount);
        Assert.Equal("stamp2", result2.SecurityStamp);
    }
}
