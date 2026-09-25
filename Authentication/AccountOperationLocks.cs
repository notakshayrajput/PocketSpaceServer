namespace PocketSpaceServer.Authentication;

// Bounded locks coordinate cleanup/approval with file transfers within this single-server app.
public sealed class AccountOperationLocks
{
    private readonly SemaphoreSlim[] locks = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<IDisposable> AcquireAsync(string id, CancellationToken cancellationToken)
    {
        var gate = locks[(int)((uint)StringComparer.Ordinal.GetHashCode(id) % (uint)locks.Length)];
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
