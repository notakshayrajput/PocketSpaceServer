namespace PocketSpaceServer.Storage;

public sealed class GlobalStorageLock
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
