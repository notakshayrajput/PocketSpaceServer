using PocketSpaceServer.Models;
using Microsoft.Extensions.Options;

namespace PocketSpaceServer.Storage;

public sealed class FileSystemBackend(IOptions<DirectorySettings> settings) : IStorageBackend
{
    private string Root => Path.GetFullPath(settings.Value.TargetDirectory);
    public string Kind => "FileSystem";

    public Task<StorageItem?> StatAsync(string path, CancellationToken cancellationToken = default)
    {
        StorageItem? item = null;
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            item = new(path, info.Name, true, 0, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
        else if (File.Exists(path))
        {
            var info = new FileInfo(path);
            item = new(path, info.Name, false, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
        return Task.FromResult(item);
    }

    public async Task<IReadOnlyList<StorageItem>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
        var result = new List<StorageItem>();
        foreach (var path in UserStorage.Entries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await StatAsync(path, cancellationToken);
            if (item is not null) result.Add(item);
        }
        return result;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public async Task WriteAsync(string path, Stream source, long length, CancellationToken cancellationToken = default)
    {
        // Keep an existing file intact if the client cancels while the copy is in progress.
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".pocketspace-upload-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(target, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true));

    public Task MoveAsync(string source, string destination, bool isFolder, CancellationToken cancellationToken = default)
    {
        if (isFolder) Directory.Move(source, destination);
        else File.Move(source, destination);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken = default)
    {
        if (isFolder && Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<long> TotalBytesAsync(CancellationToken cancellationToken = default)
    {
        long total = 0;
        if (Directory.Exists(Root))
            foreach (var file in Directory.EnumerateFiles(Root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    total = checked(total + new FileInfo(file).Length);
            }
        return Task.FromResult(total);
    }

    public Task<(long Total, long Available)?> CapacityAsync(CancellationToken cancellationToken = default)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Root)!);
        return Task.FromResult<(long, long)?>((drive.TotalSize, drive.AvailableFreeSpace));
    }
}
