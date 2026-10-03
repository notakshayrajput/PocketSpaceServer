using System.Net;
using System.Security.Claims;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Tests;

public class S3StorageBackendTests
{
    [Fact]
    public async Task BucketRootIsAdminOnlyAndAccountPrefixesRemainPrivate()
    {
        var accountId = Guid.NewGuid().ToString();
        var otherId = Guid.NewGuid().ToString();
        var paths = new UserStorage(Options.Create(new DirectorySettings
        {
            TargetDirectory = Path.Combine(Path.GetTempPath(), "PocketSpaceS3Paths", Guid.NewGuid().ToString("N"))
        }));
        using var client = new FakeS3Client();
        client.Objects["unclaimed.txt"] = 3;
        client.Objects["photos/a.jpg"] = 4;
        client.Objects[$".users/{accountId}/private.txt"] = 5;
        client.Objects[$".users/{accountId}/Case.txt"] = 1;
        client.Objects[$".users/{accountId}/case.txt"] = 2;
        client.Objects[$".users/{otherId}/other.txt"] = 6;
        client.Objects[".pocketspace-trash/old/file.txt"] = 7;
        client.Objects[".pocketspace-upload/staged"] = 9;
        using var backend = new S3StorageBackend(paths, "test-bucket", client);
        var admin = Principal(Guid.NewGuid().ToString(), "Admin");
        var user = Principal(accountId, "User");

        var adminItems = await backend.ListAsync(paths.Root(admin));
        Assert.Equal(new[] { "photos", "unclaimed.txt" }, adminItems.Select(item => item.Name).Order());
        Assert.All(adminItems, item => Assert.DoesNotContain(".users", item.Path));
        var userItems = await backend.ListAsync(paths.Root(user));
        Assert.Equal(new[] { "Case.txt", "case.txt", "private.txt" },
            userItems.Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(37, await backend.TotalBytesAsync());
        client.ListCalls = 0;
        Assert.Equal(8, await backend.ActiveBytesAsync(paths.Root(user)));
        Assert.Equal(1, client.ListCalls);
        Assert.Equal(7, await backend.ActiveBytesAsync(paths.Root(admin)));
        client.ListCalls = 0;
        Assert.Equal(new[] { "Case.txt", "case.txt", "private.txt" },
            (await backend.ActiveFilesAsync(paths.Root(user))).Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, client.ListCalls);

        await backend.MoveAsync(paths.Resolve(admin, "photos"), paths.Resolve(admin, "renamed"), true);
        Assert.False(client.Objects.ContainsKey("photos/a.jpg"));
        Assert.Equal(4, client.Objects["renamed/a.jpg"]);
    }

    [Fact]
    public async Task CancelledUploadCleansStagingAndPreservesExistingObject()
    {
        var paths = new UserStorage(Options.Create(new DirectorySettings
        {
            TargetDirectory = Path.Combine(Path.GetTempPath(), "PocketSpaceS3Paths", Guid.NewGuid().ToString("N"))
        }));
        using var client = new FakeS3Client();
        client.Objects["report.txt"] = 8;
        using var backend = new S3StorageBackend(paths, "test-bucket", client);
        using var cancellation = new CancellationTokenSource();
        client.BeforePut = () => cancellation.Cancel();
        await using var content = new MemoryStream("replacement"u8.ToArray());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            backend.WriteAsync(paths.Resolve(Principal(Guid.NewGuid().ToString(), "Admin"), "report.txt"),
                content, content.Length, cancellation.Token));

        Assert.Equal(8, client.Objects["report.txt"]);
        Assert.DoesNotContain(client.Objects.Keys, key => key.StartsWith(UserStorage.UploadDirectory + "/", StringComparison.Ordinal));

        client.BeforePut = null;
        await using var retry = new MemoryStream("replacement"u8.ToArray());
        await backend.WriteAsync(paths.Resolve(Principal(Guid.NewGuid().ToString(), "Admin"), "report.txt"),
            retry, retry.Length);
        Assert.Equal(retry.Length, client.Objects["report.txt"]);
        Assert.DoesNotContain(client.Objects.Keys, key => key.StartsWith(UserStorage.UploadDirectory + "/", StringComparison.Ordinal));

        using var cancellationDuringCopy = new CancellationTokenSource();
        client.BeforeCopy = () => cancellationDuringCopy.Cancel();
        await using var thirdAttempt = new MemoryStream("another"u8.ToArray());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            backend.WriteAsync(paths.Resolve(Principal(Guid.NewGuid().ToString(), "Admin"), "report.txt"),
                thirdAttempt, thirdAttempt.Length, cancellationDuringCopy.Token));
        Assert.Equal(retry.Length, client.Objects["report.txt"]);
        Assert.DoesNotContain(client.Objects.Keys, key => key.StartsWith(UserStorage.UploadDirectory + "/", StringComparison.Ordinal));
    }

    private static ClaimsPrincipal Principal(string id, string role)
    {
        var identity = new ClaimsIdentity("test", "name", "role");
        identity.AddClaim(new Claim("sub", id));
        identity.AddClaim(new Claim("role", role));
        return new ClaimsPrincipal(identity);
    }

    private sealed class FakeS3Client() : AmazonS3Client(new BasicAWSCredentials("key", "secret"), RegionEndpoint.USEast1)
    {
        public Dictionary<string, long> Objects { get; } = new(StringComparer.Ordinal);
        public int ListCalls { get; set; }
        public Action? BeforePut { get; set; }
        public Action? BeforeCopy { get; set; }

        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken)
        {
            BeforePut?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            Objects[request.Key] = request.InputStream.Length;
            return Task.FromResult(new PutObjectResponse());
        }

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(
            string bucketName, string key, CancellationToken cancellationToken)
        {
            if (!Objects.TryGetValue(key, out var size))
                throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound };
            return Task.FromResult(new GetObjectMetadataResponse
            {
                ContentLength = size, LastModified = DateTime.UtcNow
            });
        }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(
            ListObjectsV2Request request, CancellationToken cancellationToken)
        {
            ListCalls++;
            var objects = new List<S3Object>();
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, size) in Objects.OrderBy(item => item.Key))
            {
                if (!key.StartsWith(request.Prefix ?? "", StringComparison.Ordinal)) continue;
                var rest = key[(request.Prefix?.Length ?? 0)..];
                var slash = request.Delimiter is null ? -1 : rest.IndexOf(request.Delimiter, StringComparison.Ordinal);
                if (slash >= 0) prefixes.Add((request.Prefix ?? "") + rest[..(slash + 1)]);
                else objects.Add(new S3Object { Key = key, Size = size, LastModified = DateTime.UtcNow });
            }
            return Task.FromResult(new ListObjectsV2Response
            {
                S3Objects = objects, CommonPrefixes = prefixes.ToList(), IsTruncated = false
            });
        }

        public override Task<CopyObjectResponse> CopyObjectAsync(
            CopyObjectRequest request, CancellationToken cancellationToken)
        {
            BeforeCopy?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            Objects[request.DestinationKey] = Objects[request.SourceKey];
            return Task.FromResult(new CopyObjectResponse());
        }

        public override Task<DeleteObjectResponse> DeleteObjectAsync(
            string bucketName, string key, CancellationToken cancellationToken)
        {
            Objects.Remove(key);
            return Task.FromResult(new DeleteObjectResponse());
        }
    }
}
