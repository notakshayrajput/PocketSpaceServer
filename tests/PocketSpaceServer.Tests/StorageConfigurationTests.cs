using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Tests;

public class StorageConfigurationTests
{
    [Fact]
    public void DefaultsToApplicationStorageDirectoryAndUsEastOne()
    {
        var configuration = new ConfigurationBuilder().Build();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PocketSpaceConfiguration"));
        var options = StorageOptions.Load(configuration, root);

        Assert.Equal("FileSystem", options.Provider);
        Assert.Equal(Path.Combine(root, "StorageDirectory"), options.FileSystemPath);
        Assert.Equal("us-east-1", options.S3Region);
    }

    [Fact]
    public void S3RequiresCredentials()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PocketSpace:Storage:Provider"] = "S3",
            ["PocketSpace:Storage:S3:Bucket"] = "test-bucket"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => StorageOptions.Load(configuration, Path.GetTempPath()));
    }

    [Fact]
    public async Task AdminCanChangeOnlyLimitWhileProviderStaysConfigured()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var storage = app.Services.GetRequiredService<StorageOptions>();
        Assert.StartsWith(Path.GetTempPath(), storage.FileSystemPath);

        using var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin@123" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginToken>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        using var change = await client.PutAsJsonAsync("/api/admin/storage-settings", new
        {
            globalLimitBytes = 5L * 1024 * 1024 * 1024,
            backend = "S3",
            bucket = "should-not-apply"
        });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        using var limitChange = await client.PutAsJsonAsync("/api/admin/storage-settings", new
        {
            globalLimitBytes = 5L * 1024 * 1024 * 1024
        });
        Assert.Equal(HttpStatusCode.NoContent, limitChange.StatusCode);
        using var response = await client.GetAsync("/api/admin/storage-settings");
        response.EnsureSuccessStatusCode();
        var settings = (await response.Content.ReadFromJsonAsync<StorageSettingsRead>())!;
        Assert.Equal("FileSystem", settings.Backend);
        Assert.Equal(storage.FileSystemPath, settings.FileSystemPath);
        Assert.Equal(5L * 1024 * 1024 * 1024, settings.GlobalLimitBytes);
        Assert.Null(settings.Bucket);
    }

    private sealed record LoginToken(string AccessToken);
    private sealed record StorageSettingsRead(string Backend, string? FileSystemPath, string? Bucket,
        long? GlobalLimitBytes);
}
