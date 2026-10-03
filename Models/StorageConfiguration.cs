namespace PocketSpaceServer.Models;

public sealed class StorageConfiguration
{
    public int Id { get; set; } = 1;
    public long? GlobalLimitBytes { get; set; }
}
