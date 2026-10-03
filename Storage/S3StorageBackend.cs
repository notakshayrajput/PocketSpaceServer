using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using System.Net;

namespace PocketSpaceServer.Storage;

public sealed class S3StorageBackend : IStorageBackend, IDisposable
{
    private readonly IAmazonS3 client;
    private readonly UserStorage paths;
    private readonly string bucket;
    public string Kind => "S3";

    public S3StorageBackend(UserStorage paths, string bucket, string region, string accessKey, string secretKey)
        : this(paths, bucket, new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey),
            RegionEndpoint.GetBySystemName(region))) { }

    public S3StorageBackend(UserStorage paths, string bucket, IAmazonS3 client)
    {
        this.paths = paths;
        this.bucket = bucket;
        this.client = client;
    }

    private string Key(string path)
    {
        var relative = Path.GetRelativePath(paths.BaseRoot, path).Replace('\\', '/');
        if (relative == ".") return "";
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new ArgumentException("Invalid storage path.");
        return relative;
    }

    private string PathFor(string key) => UserStorage.ResolveUnder(paths.BaseRoot, key.TrimEnd('/'));
    private static bool Visible(string key) => key.Split('/', StringSplitOptions.RemoveEmptyEntries)
        .All(part => !part.Equals(UserStorage.UsersDirectory, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            !part.Equals(UserStorage.TrashDirectory, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            !part.Equals(UserStorage.UploadDirectory, StringComparison.Ordinal));

    private async Task<List<S3Object>> ObjectsAsync(string prefix, CancellationToken cancellationToken)
    {
        var objects = new List<S3Object>();
        string? continuation = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket, Prefix = prefix, ContinuationToken = continuation
            }, cancellationToken);
            objects.AddRange(response.S3Objects ?? []);
            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuation is not null);
        return objects;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default) =>
        await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket, MaxKeys = 1
        }, cancellationToken);

    public async Task<StorageItem?> StatAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = Key(path);
        if (key == "") return new(path, "My files", true, 0, DateTime.UtcNow, DateTime.UtcNow);
        var parts = key.Split('/');
        if (parts.Length == 2 && parts[0] == UserStorage.UsersDirectory && Guid.TryParse(parts[1], out _))
            return new(path, parts[1], true, 0, DateTime.UtcNow, DateTime.UtcNow);
        try
        {
            var file = await client.GetObjectMetadataAsync(bucket, key, cancellationToken);
            var modified = file.LastModified ?? DateTime.UtcNow;
            return new(path, Path.GetFileName(path), false, file.ContentLength, modified, modified);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
        var children = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket, Prefix = key + "/", MaxKeys = 1
        }, cancellationToken);
        if ((children.S3Objects?.Count ?? 0) == 0 && (children.CommonPrefixes?.Count ?? 0) == 0) return null;
        var changed = children.S3Objects?.FirstOrDefault()?.LastModified ?? DateTime.UtcNow;
        return new(path, Path.GetFileName(path), true, 0, changed, changed);
    }

    public async Task<IReadOnlyList<StorageItem>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        var parent = await StatAsync(directory, cancellationToken);
        if (parent is null || !parent.IsFolder) throw new DirectoryNotFoundException();
        var prefix = Key(directory);
        if (prefix != "") prefix += "/";
        var result = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
        string? continuation = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket, Prefix = prefix, Delimiter = "/", ContinuationToken = continuation
            }, cancellationToken);
            foreach (var objectKey in response.S3Objects ?? [])
            {
                if (objectKey.Key == prefix || objectKey.Key.Contains('\\') ||
                    !Visible(objectKey.Key[prefix.Length..])) continue;
                var changed = objectKey.LastModified ?? DateTime.UtcNow;
                string path;
                try { path = PathFor(objectKey.Key); }
                catch (ArgumentException) { continue; }
                result[objectKey.Key] = new(path, Path.GetFileName(path), false, objectKey.Size ?? 0, changed, changed);
            }
            var folderPrefixes = (response.CommonPrefixes ?? [])
                .Where(childPrefix => !childPrefix.Contains('\\') && Visible(childPrefix[prefix.Length..]))
                .ToArray();
            foreach (var batch in folderPrefixes.Chunk(8))
            {
                var folders = await Task.WhenAll(batch.Select(async childPrefix =>
                {
                    try { return (Prefix: childPrefix, Item: await StatAsync(PathFor(childPrefix), cancellationToken)); }
                    catch (ArgumentException) { return (Prefix: childPrefix, Item: (StorageItem?)null); }
                }));
                foreach (var folder in folders)
                    if (folder.Item is not null) result[folder.Prefix] = folder.Item;
            }
            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuation is not null);
        return result.Values.ToArray();
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = Key(path);
        if (key == "") return;
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key + "/", ContentBody = "" }, cancellationToken);
    }

    public async Task WriteAsync(string path, Stream source, long length, CancellationToken cancellationToken = default)
    {
        // Publish only after the upload is complete. A cancelled transfer leaves the old object intact.
        var stagedKey = $"{UserStorage.UploadDirectory}/{Guid.NewGuid():N}";
        try
        {
            using var transfer = new TransferUtility(client);
            await transfer.UploadAsync(new TransferUtilityUploadRequest
            {
                BucketName = bucket, Key = stagedKey, InputStream = source, PartSize = 8L * 1024 * 1024,
                AutoCloseStream = false
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await CopyAsync(stagedKey, Key(path), length, cancellationToken);
        }
        finally
        {
            // Cleanup must run even after the HTTP request token is cancelled.
            await client.DeleteObjectAsync(bucket, stagedKey, CancellationToken.None);
        }
    }

    public async Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try { return (await client.GetObjectAsync(bucket, Key(path), cancellationToken)).ResponseStream; }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { throw new FileNotFoundException(); }
    }

    public async Task MoveAsync(string source, string destination, bool isFolder, CancellationToken cancellationToken = default)
    {
        var from = Key(source);
        var to = Key(destination);
        var objects = isFolder ? await ObjectsAsync(from + "/", cancellationToken)
            : [new S3Object { Key = from, Size = (await client.GetObjectMetadataAsync(bucket, from, cancellationToken)).ContentLength }];
        var copied = new List<string>();
        try
        {
            foreach (var item in objects)
            {
                var target = to + item.Key[from.Length..];
                await CopyAsync(item.Key, target, item.Size ?? 0, cancellationToken);
                copied.Add(target);
            }
        }
        catch
        {
            foreach (var key in copied)
                await client.DeleteObjectAsync(bucket, key, CancellationToken.None);
            throw;
        }
        foreach (var item in objects)
            await client.DeleteObjectAsync(bucket, item.Key, cancellationToken);
    }

    private async Task CopyAsync(string source, string destination, long size, CancellationToken cancellationToken)
    {
        if (size <= 5_000_000_000L)
        {
            await client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket, SourceKey = source, DestinationBucket = bucket,
                DestinationKey = destination
            }, cancellationToken);
            return;
        }

        var upload = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket, Key = destination
        }, cancellationToken);
        try
        {
            const long partSize = 512L * 1024 * 1024;
            var parts = new List<PartETag>();
            for (long start = 0; start < size; start += partSize)
            {
                var number = parts.Count + 1;
                var copied = await client.CopyPartAsync(new CopyPartRequest
                {
                    SourceBucket = bucket, SourceKey = source,
                    DestinationBucket = bucket, DestinationKey = destination,
                    UploadId = upload.UploadId, PartNumber = number,
                    FirstByte = start, LastByte = Math.Min(size - 1, start + partSize - 1)
                }, cancellationToken);
                parts.Add(new PartETag(number, copied.ETag));
            }
            await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket, Key = destination, UploadId = upload.UploadId, PartETags = parts
            }, cancellationToken);
        }
        catch
        {
            await client.AbortMultipartUploadAsync(bucket, destination, upload.UploadId, CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(string path, bool isFolder, CancellationToken cancellationToken = default)
    {
        var key = Key(path);
        if (isFolder)
        {
            var prefix = key == "" ? "" : key + "/";
            foreach (var item in await ObjectsAsync(prefix, cancellationToken))
                await client.DeleteObjectAsync(bucket, item.Key, cancellationToken);
        }
        else await client.DeleteObjectAsync(bucket, key, cancellationToken);
    }

    public async Task<long> TotalBytesAsync(CancellationToken cancellationToken = default) =>
        (await ObjectsAsync("", cancellationToken)).Sum(item => item.Size ?? 0);

    public async Task<long> ActiveBytesAsync(string directory, CancellationToken cancellationToken = default)
    {
        var prefix = Key(directory);
        if (prefix != "") prefix += "/";
        return (await ObjectsAsync(prefix, cancellationToken))
            .Where(item => Visible(item.Key[prefix.Length..]))
            .Sum(item => item.Size ?? 0);
    }

    public async Task<IReadOnlyList<StorageItem>> ActiveFilesAsync(string directory, CancellationToken cancellationToken = default)
    {
        var prefix = Key(directory);
        if (prefix != "") prefix += "/";
        var result = new List<StorageItem>();
        foreach (var item in await ObjectsAsync(prefix, cancellationToken))
        {
            if (item.Key.Length <= prefix.Length || item.Key.EndsWith('/') ||
                !Visible(item.Key[prefix.Length..])) continue;
            string path;
            try { path = PathFor(item.Key); }
            catch (ArgumentException) { continue; }
            var modified = item.LastModified ?? DateTime.UtcNow;
            result.Add(new StorageItem(path, Path.GetFileName(path), false, item.Size ?? 0, modified, modified));
        }
        return result;
    }

    public Task<(long Total, long Available)?> CapacityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<(long, long)?>(null);

    public void Dispose() => client.Dispose();
}
