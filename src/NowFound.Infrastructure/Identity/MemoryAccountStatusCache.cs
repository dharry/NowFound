using Microsoft.Extensions.Caching.Memory;
using NowFound.Application.Users;

namespace NowFound.Infrastructure.Identity;

public sealed class MemoryAccountStatusCache(IMemoryCache cache) : IAccountStatusCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    private static string Key(string userId) => $"account_status:{userId}";

    public async ValueTask<AccountStateSnapshot> GetOrLoadAsync(
        string userId,
        Func<string, CancellationToken, Task<AccountStateSnapshot>> factory,
        CancellationToken ct = default)
    {
        var key = Key(userId);
        if (cache.TryGetValue(key, out AccountStateSnapshot? snapshot) && snapshot is not null)
        {
            return snapshot;
        }

        snapshot = await factory(userId, ct);
        cache.Set(key, snapshot, DefaultTtl);
        return snapshot;
    }

    public void Invalidate(string userId)
    {
        cache.Remove(Key(userId));
    }
}
