namespace PocketSpaceServer.Utility
{
    public static class Helper
    {
        public static long GetDirectorySize(DirectoryInfo directoryInfo)
        {
            long size = 0;

            // Add file sizes
            FileInfo[] files = directoryInfo.GetFiles();
            foreach (var file in files)
            {
                size += file.Length;
            }

            // Add subdirectory sizes
            DirectoryInfo[] dirs = directoryInfo.GetDirectories();
            foreach (var dir in dirs)
            {
                size += GetDirectorySize(dir);
            }

            return size;
        }

    }
}
