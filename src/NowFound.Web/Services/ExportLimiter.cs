namespace NowFound.Web.Services;

/// <summary>
/// エクスポートの同時実行数を制限する。全件を読み続けるため、増えると DB の読み取りと帯域を圧迫する。
/// 待たせずに断る（呼び出し側が 429 を返す）。
/// </summary>
public sealed class ExportLimiter(int maxConcurrent = ExportLimiter.DefaultMaxConcurrent)
{
    public const int DefaultMaxConcurrent = 2;

    private readonly SemaphoreSlim _slots = new(maxConcurrent, maxConcurrent);

    /// <summary>空きがあれば枠を確保して返す（Dispose で解放）。なければ null。</summary>
    public IDisposable? TryEnter() => _slots.Wait(0) ? new Lease(_slots) : null;

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
