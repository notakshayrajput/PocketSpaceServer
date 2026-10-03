using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Tests;

public class FolderListingTests
{
    [Fact]
    public void StoredNaturalKeysMatchExistingNameSort()
    {
        var names = new[] { "file2", "file10", "file02", "File1", "file0", "file00",
            "file-A", "file.a", "résumé2", "Résumé10", "a2.txt", "a10.txt" };
        var entries = names.Select(name => new FolderListing.Entry(name, name, false, 0, default, default));
        var expected = FolderListing.Sort(entries, "name", "asc").Select(entry => entry.Name);
        var actual = names.OrderBy(FolderListing.NaturalSortKey, StringComparer.Ordinal)
            .ThenBy(FolderListing.OrdinalSortKey, StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Report-final.pdf", "port fin", true)]
    [InlineData("Report-final.pdf", "repotr finl", true)]
    [InlineData("Report.pdf", "repotr.pdf", true)]
    [InlineData("Résumé.pdf", "resume", true)]
    [InlineData("Report-final.pdf", "holiday", false)]
    [InlineData("Report-final.pdf", "r", true)]
    public void FilenameSearchHandlesPartialNamesAndSmallTypos(string name, string search, bool matches)
    {
        Assert.Equal(matches, FolderListing.MatchesName(name, search));
    }
}
