using System.Security.Claims;
using Microsoft.Extensions.Options;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Storage;

public sealed class UserStorage(IOptions<DirectorySettings> settings)
{
    private string BaseRoot => Path.GetFullPath(settings.Value.TargetDirectory);
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public const string UsersDirectory = ".users";

    public string UserRoot(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new InvalidOperationException("Invalid account storage ID.");
        return ResolveUnder(BaseRoot, $"{UsersDirectory}/{id}");
    }

    // Preserve the administrator's existing storage. New accounts have private roots.
    public string Root(ClaimsPrincipal user) => user.IsInRole("Admin")
        ? ResolveUnder(BaseRoot, ".") : UserRoot(user.FindFirst("sub")!.Value);

    public string Resolve(ClaimsPrincipal user, string? relativePath)
    {
        var root = Root(user);
        var full = ResolveUnder(root, relativePath);
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        // Managed account roots cannot be browsed, moved, or deleted through the legacy admin drive.
        if (user.IsInRole("Admin") && (relative.Equals(UsersDirectory, PathComparison) ||
            relative.StartsWith(UsersDirectory + "/", PathComparison)))
            throw new ArgumentException("Invalid storage path.");
        return full;
    }

    public static string ResolveUnder(string root, string? relativePath)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        relativePath = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.Split('/').Contains(".."))
            throw new ArgumentException("Invalid storage path.");
        if (relativePath.Split('/').Any(part => part != "." && (part.EndsWith('.') || part.EndsWith(' '))))
            throw new ArgumentException("Invalid storage path.");
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.Equals(root, PathComparison) && !full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            throw new ArgumentException("Invalid storage path.");

        // Reject symlinks/junctions in every component, including ancestors of the configured root.
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Symbolic links are not supported in storage.");
        }
        return full;
    }

    public static IEnumerable<string> Entries(string directory) => Directory.EnumerateFileSystemEntries(directory)
        .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
            !Path.GetFileName(path).Equals(UsersDirectory, PathComparison));

    public static IEnumerable<string> FilesRecursively(string directory)
    {
        foreach (var path in Entries(directory))
        {
            if (Directory.Exists(path))
            {
                foreach (var child in FilesRecursively(path)) yield return child;
            }
            else yield return path;
        }
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(':') ||
            name.EndsWith('.') || name.EndsWith(' ') || name.Equals(UsersDirectory, PathComparison))
            throw new ArgumentException("Invalid file or folder name.");
    }
}
