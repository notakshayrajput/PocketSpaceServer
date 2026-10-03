namespace PocketSpaceServer.Storage;

public sealed record StorageItem(string Path, string Name, bool IsFolder, long Size,
    DateTime LastModified, DateTime CreatedAt);

public interface IStorageBackend
{
    string Kind { get; }
    Task<StorageItem?> StatAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StorageItem>> ListAsync(string directory, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task WriteAsync(string path, Stream source, long length, CancellationToken cancellationToken = default);
    Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default);
    Task MoveAsync(string source, string destination, bool isFolder, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken = default);
    Task<long> TotalBytesAsync(CancellationToken cancellationToken = default);
    Task<(long Total, long Available)?> CapacityAsync(CancellationToken cancellationToken = default);
}

public static class StorageBackendExtensions
{
    public static async Task<IReadOnlyList<StorageItem>> TreeAsync(this IStorageBackend backend, string directory,
        CancellationToken cancellationToken = default)
    {
        var result = new List<StorageItem>();
        foreach (var item in await backend.ListAsync(directory, cancellationToken))
        {
            result.Add(item);
            if (item.IsFolder) result.AddRange(await backend.TreeAsync(item.Path, cancellationToken));
        }
        return result;
    }
}
