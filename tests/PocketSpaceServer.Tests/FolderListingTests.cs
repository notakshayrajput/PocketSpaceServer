using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Tests;

public class FolderListingTests
{
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
