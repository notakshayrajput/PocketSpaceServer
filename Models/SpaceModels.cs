namespace PocketSpaceServer.Models
{
    public class DriveStats
    {
        public required string Directory { get; set; }
        public long AvailableSpace { get; set; }
        public long TotalSpace { get; set; }
        public long OccupiedSpace { get; set; }
    }
    public class FolderInfo
    {
        public string Name { get; set; } = null!;
        public DateTime LastModified { get; set; }
        public string RelativePath { get; set; } = null!;
        public FileSystemEntry[] Files { get; set; }
    }
    public class FileSystemEntry
    {
        public string Id { get; set; } = null!;
        public bool IsFavorite { get; set; }
        public DateTime RecentAt { get; set; }
        public string Name { get; set; } = null!;
        public bool IsFolder { get; set; }
        public long Size { get; set; } // in bytes, 0 for folders
        public DateTime LastModified { get; set; }
        public string RelativePath { get; set; } = null!;
    }

}
