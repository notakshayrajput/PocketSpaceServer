using Microsoft.Extensions.Options;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Tests;

public class FileSystemBackendCancellationTests
{
    [Fact]
    public async Task CancelledReplacementPreservesExistingFileAndRemovesTemporaryCopy()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pocketspace-write-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.txt");
            await File.WriteAllTextAsync(path, "original");
            var backend = new FileSystemBackend(Options.Create(new DirectorySettings { TargetDirectory = directory }));
            using var cancellation = new CancellationTokenSource();
            await using var source = new CancellingStream(new byte[200_000], cancellation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.WriteAsync(path, source, source.Length, cancellation.Token));

            Assert.Equal("original", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, ".pocketspace-upload-*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            cancellation.Cancel();
            return read;
        }
    }
}
