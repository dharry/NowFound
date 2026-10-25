using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NowFound.Application.Information;
using NowFound.Infrastructure.Data;

namespace NowFound.Infrastructure.Information;

/// <summary>フッター用の情報。files.db の最終更新は IndexFolderStates の最新の走査時刻（MAX）で求め、全ページ分の問い合わせを避けるため短くキャッシュする。</summary>
public sealed class FooterInfoService(FilesDbContext filesDb, IMemoryCache cache) : IFooterInfoService
{
    internal const string CacheKey = "footer-files-updated";

    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task<FooterInfo> GetAsync(CancellationToken ct = default)
    {
        var updated = await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await filesDb.IndexFolderStates.AsNoTracking().MaxAsync(s => (DateTime?)s.LastScanUtc, ct);
        });

        return new FooterInfo(Environment.MachineName, updated);
    }
}
