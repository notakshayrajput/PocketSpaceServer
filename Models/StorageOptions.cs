namespace PocketSpaceServer.Models;

public sealed class StorageOptions
{
    public string Provider { get; private init; } = "FileSystem";
    public string FileSystemPath { get; private init; } = string.Empty;
    public string? S3Bucket { get; private init; }
    public string S3Region { get; private init; } = "us-east-1";
    public string? S3AccessKey { get; private init; }
    public string? S3SecretKey { get; private init; }
    public bool HasS3Credentials => !string.IsNullOrWhiteSpace(S3Bucket) &&
        !string.IsNullOrWhiteSpace(S3AccessKey) && !string.IsNullOrWhiteSpace(S3SecretKey);

    public static StorageOptions Load(IConfiguration configuration, string contentRoot)
    {
        var section = configuration.GetSection("PocketSpace:Storage");
        var provider = section["Provider"]?.Trim();
        if (string.IsNullOrEmpty(provider)) provider = "FileSystem";
        if (provider is not ("FileSystem" or "S3"))
            throw new InvalidOperationException("PocketSpace storage provider must be FileSystem or S3.");

        var configuredPath = section["FileSystemPath"]?.Trim();
        var path = string.IsNullOrEmpty(configuredPath)
            ? Path.Combine(contentRoot, "StorageDirectory")
            : Path.GetFullPath(configuredPath, contentRoot);
        var options = new StorageOptions
        {
            Provider = provider,
            FileSystemPath = path,
            S3Bucket = section["S3:Bucket"]?.Trim(),
            S3Region = section["S3:Region"]?.Trim() is { Length: > 0 } region ? region : "us-east-1",
            S3AccessKey = section["S3:AccessKey"]?.Trim(),
            S3SecretKey = section["S3:SecretKey"]
        };
        if (provider == "S3" && !options.HasS3Credentials)
            throw new InvalidOperationException("S3 storage requires a bucket, access key, and secret key.");
        return options;
    }
}
