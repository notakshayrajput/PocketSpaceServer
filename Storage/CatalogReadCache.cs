using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace PocketSpaceServer.Storage;

public sealed class CatalogReadCache(IMemoryCache cache)
{
    private sealed class AccountState
    {
        public int Version;
        public CancellationTokenSource Changed = new();
    }

    private readonly ConcurrentDictionary<string, AccountState> accounts = new();
    private static string Account(ClaimsPrincipal user, string backend) =>
        backend + ":" + user.FindFirst("sub")!.Value;

    public async Task<T> GetAsync<T>(ClaimsPrincipal user, string backend, string key,
        TimeSpan lifetime, Func<Task<T>> load)
    {
        var account = Account(user, backend);
        var cacheKey = account + ":" + key;
        var state = accounts.GetOrAdd(account, _ => new AccountState());
        int version;
        CancellationToken token;
        lock (state)
        {
            if (cache.TryGetValue(cacheKey, out T? hit) && hit is not null) return hit;
            version = state.Version;
            token = state.Changed.Token;
        }
        var value = await load();
        lock (state)
        {
            if (version == state.Version)
                cache.Set(cacheKey, value, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = lifetime, Size = 1
                }.AddExpirationToken(new CancellationChangeToken(token)));
        }
        return value;
    }

    public void Invalidate(ClaimsPrincipal user, string backend)
    {
        var account = Account(user, backend);
        var state = accounts.GetOrAdd(account, _ => new AccountState());
        lock (state)
        {
            state.Version++;
            state.Changed.Cancel();
            state.Changed.Dispose();
            state.Changed = new CancellationTokenSource();
        }
    }
}
