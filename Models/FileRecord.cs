namespace PocketSpaceServer.Models;

public sealed class FileRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = null!;
    public string RelativePath { get; set; } = null!;
    public string PathKey { get; set; } = null!;
    public bool IsFolder { get; set; }
    public bool IsFavorite { get; set; }
    public DateTime RecentAt { get; set; }
    public string? TrashEntryId { get; set; }
}

public sealed class TrashEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = null!;
    public string OriginalPath { get; set; } = null!;
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public DateTime TrashedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    // Durable intent lets a restart finish an interrupted filesystem/database operation.
    public string State { get; set; } = TrashState.Moving;
}

public static class TrashState
{
    public const string Moving = "Moving";
    public const string Trashed = "Trashed";
    public const string Restoring = "Restoring";
    public const string Purging = "Purging";
}
