namespace PocketSpaceServer.Authentication;

// Bounded locks coordinate cleanup/approval with file transfers within this single-server app.
public sealed class AccountOperationLocks
{
    private readonly SemaphoreSlim[] locks = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private SemaphoreSlim Gate(string id) => locks[(int)((uint)StringComparer.Ordinal.GetHashCode(id) % (uint)locks.Length)];

    public async Task<IDisposable> AcquireAsync(string id, CancellationToken cancellationToken)
    {
        var gate = Gate(id);
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    public IDisposable? TryAcquire(string id)
    {
        var gate = Gate(id);
        return gate.Wait(0) ? new Lease(gate) : null;
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
