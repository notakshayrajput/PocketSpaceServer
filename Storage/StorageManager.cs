using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Storage;

public sealed class StorageManager(ApplicationDbContext db, FileSystemBackend files,
    StorageOptions options, S3StorageBackend? s3 = null)
{
    private StorageConfiguration? configuration;

    public async Task<StorageConfiguration> SettingsAsync() => configuration ??=
        await db.StorageConfigurations.SingleAsync(item => item.Id == 1);

    public Task<IStorageBackend> CurrentAsync() => ForKindAsync(options.Provider);

    public IStorageBackend For(string kind) => kind switch
    {
        "FileSystem" => files,
        "S3" when options.HasS3Credentials && s3 is not null => s3,
        _ => throw new InvalidOperationException("Storage provider is not configured.")
    };

    public Task<IStorageBackend> ForKindAsync(string kind) => Task.FromResult(For(kind));
}
