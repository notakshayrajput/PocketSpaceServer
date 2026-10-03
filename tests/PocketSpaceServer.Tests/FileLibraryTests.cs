using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PocketSpaceServer.Controllers;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Tests;

public class FileLibraryTests
{
    [Fact]
    public async Task FolderListingPagesAndSearchesAcrossTheEntireFolder()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client, "alice");
        using var scope = app.Services.CreateScope();
        var root = scope.ServiceProvider.GetRequiredService<UserStorage>().Root(Principal(user));
        for (var i = 0; i < 115; i++)
            await File.WriteAllTextAsync(Path.Combine(root, $"item{i:000}.txt"), new string('x', i + 1));
        await File.WriteAllTextAsync(Path.Combine(root, "Report-final.pdf"), "x");

        var first = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info?sortBy=name&direction=asc"))!;
        Assert.Equal(50, first.Files.Length);
        Assert.Equal(116, first.TotalCount);
        Assert.Equal(50, first.NextOffset);
        Assert.True(first.HasMore);
        Assert.Equal("item000.txt", first.Files[0].Name);
        Assert.Equal("item049.txt", first.Files[^1].Name);
        Assert.Equal(116, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Files.CountAsync());

        var second = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info?sortBy=name&direction=asc&offset=50"))!;
        var third = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info?sortBy=name&direction=asc&offset=100"))!;
        Assert.Equal(50, second.Files.Length);
        Assert.Equal(16, third.Files.Length);
        Assert.False(third.HasMore);
        Assert.Equal(116, third.NextOffset);
        Assert.Equal(116, first.Files.Concat(second.Files).Concat(third.Files).Select(file => file.Name).Distinct().Count());

        var fuzzy = (await client.GetFromJsonAsync<FolderInfo>(
            "/api/space/folder-info?search=repotr%20finl&sortBy=name&direction=asc"))!;
        Assert.Equal("Report-final.pdf", Assert.Single(fuzzy.Files).Name);
        Assert.Equal(1, fuzzy.TotalCount);
        Assert.False(fuzzy.HasMore);
        var noMatches = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info?search=unfindable"))!;
        Assert.Empty(noMatches.Files);
        Assert.Equal(0, noMatches.TotalCount);
        Assert.False(noMatches.HasMore);
    }

    [Fact]
    public async Task FolderListingKeepsFoldersFirstForEverySortAndSearch()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client, "alice");
        using var scope = app.Services.CreateScope();
        var root = scope.ServiceProvider.GetRequiredService<UserStorage>().Root(Principal(user));
        Directory.CreateDirectory(Path.Combine(root, "a-report"));
        Directory.CreateDirectory(Path.Combine(root, "z-report"));
        await File.WriteAllTextAsync(Path.Combine(root, "b-report.txt"), "small");
        await File.WriteAllTextAsync(Path.Combine(root, "m-report.txt"), "larger file");

        foreach (var sortBy in new[] { "name", "size", "lastModified", "createdAt" })
        foreach (var direction in new[] { "asc", "desc" })
        foreach (var search in new[] { "", "repotr" })
        {
            var options = $"sortBy={sortBy}&direction={direction}&search={search}&limit=2";
            var first = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info?" + options))!;
            var second = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info?" + options + "&offset=2"))!;
            Assert.Equal(4, first.TotalCount);
            Assert.Equal(2, first.Files.Length);
            Assert.All(first.Files, entry => Assert.True(entry.IsFolder));
            Assert.True(first.HasMore);
            Assert.Equal(2, second.Files.Length);
            Assert.All(second.Files, entry => Assert.False(entry.IsFolder));
            Assert.False(second.HasMore);
            if (sortBy == "name")
                Assert.Equal(direction == "asc" ? new[] { "a-report", "z-report" } : new[] { "z-report", "a-report" },
                    first.Files.Select(entry => entry.Name));
        }
    }

    [Fact]
    public async Task FolderListingSortsBeforePagingAndRejectsInvalidOptions()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client, "alice");
        using var scope = app.Services.CreateScope();
        var root = scope.ServiceProvider.GetRequiredService<UserStorage>().Root(Principal(user));
        foreach (var (name, size, minutes) in new[] { ("a10.txt", 30, 1), ("a2.txt", 10, 3), ("z.txt", 20, 2) })
        {
            var path = Path.Combine(root, name);
            await File.WriteAllTextAsync(path, new string('x', size));
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, minutes, 0, DateTimeKind.Utc));
        }

        var byName = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info?sortBy=name&direction=asc&limit=2"))!;
        Assert.Equal(new[] { "a2.txt", "a10.txt" }, byName.Files.Select(file => file.Name));
        Assert.Equal("z.txt", Assert.Single((await client.GetFromJsonAsync<FolderInfo>(
            "/api/space/folder-info?sortBy=name&direction=asc&limit=2&offset=2"))!.Files).Name);
        var bySize = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info?sortBy=size&direction=desc"))!;
        Assert.Equal(new[] { "a10.txt", "z.txt", "a2.txt" }, bySize.Files.Select(file => file.Name));
        var byModified = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info?sortBy=lastModified&direction=desc"))!;
        Assert.Equal(new[] { "a2.txt", "z.txt", "a10.txt" }, byModified.Files.Select(file => file.Name));
        var byCreated = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info?sortBy=createdAt&direction=asc"))!;
        Assert.True(byCreated.Files.Zip(byCreated.Files.Skip(1)).All(pair => pair.First.CreatedAt <= pair.Second.CreatedAt));

        foreach (var options in new[] { "limit=51", "offset=-1", "sortBy=unknown", "direction=sideways" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/space/folder-info?" + options)).StatusCode);
    }

    [Fact]
    public async Task FolderListingIncludesCreationTimesForFilesAndFolders()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client);
        (await client.PostAsJsonAsync("/api/space/folders", new { parentPath = ".", name = "photos" })).EnsureSuccessStatusCode();
        await Upload(client, "trip.txt", "holiday");

        var files = (await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info"))!.Files;
        using var scope = app.Services.CreateScope();
        var root = scope.ServiceProvider.GetRequiredService<UserStorage>().Root(Principal(user));
        var file = Assert.Single(files, item => item.Name == "trip.txt");
        var folder = Assert.Single(files, item => item.Name == "photos");
        Assert.Equal(File.GetCreationTimeUtc(Path.Combine(root, "trip.txt")), file.CreatedAt);
        Assert.Equal(Directory.GetCreationTimeUtc(Path.Combine(root, "photos")), folder.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, file.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, folder.CreatedAt.Kind);
    }

    [Fact]
    public async Task FavoritesPersistAcrossRequestsAndRenameOfParentFolder()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await SignIn(client);
        (await client.PostAsJsonAsync("/api/space/folders", new { parentPath = ".", name = "photos" })).EnsureSuccessStatusCode();
        await Upload(client, "trip.txt", "holiday", "photos");
        var file = Assert.Single((await Home(client)).Recent);
        await Favorite(client, file.Id, true);
        (await client.PostAsJsonAsync("/api/space/rename", new { path = "photos", name = "travel" })).EnsureSuccessStatusCode();
        var favorite = Assert.Single((await Home(client)).Favorites);
        Assert.Equal(file.Id, favorite.Id);
        Assert.Equal("travel/trip.txt", favorite.RelativePath);
        using var anotherClient = app.CreateClient();
        await SignIn(anotherClient);
        Assert.Equal(file.Id, Assert.Single((await Home(anotherClient)).Favorites).Id);
        (await anotherClient.PostAsJsonAsync("/api/space/rename", new { path = "travel/trip.txt", name = "memories.txt" })).EnsureSuccessStatusCode();
        Assert.Equal("travel/memories.txt", Assert.Single((await Home(client)).Favorites).RelativePath);
        await Favorite(client, file.Id, false);
        Assert.Empty((await Home(client)).Favorites);
    }

    [Fact]
    public async Task RecentTracksUploadsAndDownloadsWithoutFavoriteChangingOrder()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await SignIn(client);
        await Upload(client, "first.txt", "first");
        var first = Assert.Single((await Home(client)).Recent);
        app.Clock.Now = app.Clock.Now.AddMinutes(1);
        await Upload(client, "second.txt", "second");
        Assert.Equal("second.txt", (await Home(client)).Recent[0].Name);
        await Favorite(client, first.Id, true);
        Assert.Equal("second.txt", (await Home(client)).Recent[0].Name);
        app.Clock.Now = app.Clock.Now.AddMinutes(1);
        using var download = await client.PostAsJsonAsync("/api/download", new { paths = new[] { "first.txt" } });
        download.EnsureSuccessStatusCode();
        Assert.Equal("first", await download.Content.ReadAsStringAsync());
        Assert.Equal(first.Id, (await Home(client)).Recent[0].Id);
    }

    [Fact]
    public async Task ExistingDiskFilesAreImportedAndRecentIsLimitedToTwenty()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client);
        using var scope = app.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<UserStorage>();
        var root = storage.Root(Principal(user));
        Directory.CreateDirectory(Path.Combine(root, "legacy"));
        for (var i = 0; i < 25; i++)
        {
            var path = Path.Combine(root, "legacy", $"file{i}.txt");
            await File.WriteAllTextAsync(path, "existing");
            File.SetLastWriteTimeUtc(path, app.Clock.Now.UtcDateTime.AddMinutes(i - 25));
        }
        await scope.ServiceProvider.GetRequiredService<CatalogReconciler>()
            .RefreshAsync(Principal(user), CancellationToken.None);
        var home = await Home(client);
        Assert.Equal(20, home.Recent.Length);
        Assert.Equal("file24.txt", home.Recent[0].Name);
        await Favorite(client, home.Recent[0].Id, true);
        Assert.Single((await Home(client)).Favorites);
    }

    [Fact]
    public async Task ReconciliationEvictsCachedPagesAfterExternalChanges()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client);
        Assert.Empty((await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info"))!.Files);
        Assert.Empty((await Home(client)).Recent);

        using var scope = app.Services.CreateScope();
        var principal = Principal(user);
        var root = scope.ServiceProvider.GetRequiredService<UserStorage>().Root(principal);
        var external = Path.Combine(root, "external.txt");
        await File.WriteAllTextAsync(external, "new content");
        await scope.ServiceProvider.GetRequiredService<CatalogReconciler>()
            .RefreshAsync(principal, CancellationToken.None);
        Assert.Equal("external.txt", Assert.Single((await client.GetFromJsonAsync<FolderInfo>(
            "/api/space/folder-info"))!.Files).Name);
        var discovered = Assert.Single((await Home(client)).Recent);
        Assert.Equal("external.txt", discovered.Name);
        await Favorite(client, discovered.Id, true);

        await File.WriteAllTextAsync(external, "new and longer content");
        await scope.ServiceProvider.GetRequiredService<CatalogReconciler>()
            .RefreshAsync(principal, CancellationToken.None);
        Assert.Equal(new FileInfo(external).Length, Assert.Single((await client.GetFromJsonAsync<FolderInfo>(
            "/api/space/folder-info"))!.Files).Size);

        File.Delete(external);
        await scope.ServiceProvider.GetRequiredService<CatalogReconciler>()
            .RefreshAsync(principal, CancellationToken.None);
        Assert.Empty((await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info"))!.Files);
        Assert.Empty((await Home(client)).Recent);
        Assert.Empty((await Home(client)).Favorites);

        await File.WriteAllTextAsync(external, "returned");
        await scope.ServiceProvider.GetRequiredService<CatalogReconciler>()
            .RefreshAsync(principal, CancellationToken.None);
        var returned = Assert.Single((await Home(client)).Favorites);
        Assert.Equal(discovered.Id, returned.Id);
    }

    [Fact]
    public async Task ReconciliationBackfillsExistingRecordWithoutLosingFavoriteOrIdentity()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client);
        await Upload(client, "legacy.txt", "existing bytes");
        var original = Assert.Single((await Home(client)).Recent);
        await Favorite(client, original.Id, true);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Files.Where(file => file.Id == original.Id).ExecuteUpdateAsync(update => update
            .SetProperty(file => file.Name, "")
            .SetProperty(file => file.ParentPathKey, "")
            .SetProperty(file => file.NameSortKey, "")
            .SetProperty(file => file.OrdinalNameSortKey, "")
            .SetProperty(file => file.Size, 0L)
            .SetProperty(file => file.LastModified, DateTime.MinValue)
            .SetProperty(file => file.CreatedAt, DateTime.MinValue));
        await scope.ServiceProvider.GetRequiredService<CatalogReconciler>()
            .RefreshAsync(Principal(user), CancellationToken.None);

        var restored = Assert.Single((await Home(client)).Favorites);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal("legacy.txt", restored.Name);
        Assert.Equal("existing bytes".Length, restored.Size);
        Assert.Equal(original.Id, Assert.Single((await client.GetFromJsonAsync<FolderInfo>(
            "/api/space/folder-info"))!.Files).Id);
    }

    [Fact]
    public async Task TrashHidesFilesAndRestorePreservesBytesFavoriteAndIdentity()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await SignIn(client);
        await Upload(client, "keep.txt", "original bytes");
        var file = Assert.Single((await Home(client)).Recent);
        await Favorite(client, file.Id, true);
        (await client.DeleteAsync("/api/space/entry?path=keep.txt")).EnsureSuccessStatusCode();
        Assert.Empty((await Home(client)).Recent);
        Assert.Empty((await Home(client)).Favorites);
        Assert.Empty((await client.GetFromJsonAsync<FolderInfo>("/api/space/folder-info"))!.Files);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/download", new { paths = new[] { "keep.txt" } })).StatusCode);
        var trash = Assert.Single(await Trash(client));
        Assert.Equal("keep.txt", trash.OriginalPath);
        Assert.Equal(TimeSpan.FromDays(7), trash.ExpiresAt - trash.TrashedAt);
        Assert.Equal(DateTimeKind.Utc, trash.ExpiresAt.Kind);
        app.Clock.Now = app.Clock.Now.AddDays(6);
        (await client.PostAsJsonAsync($"/api/space/trash/{trash.Id}/restore", new { })).EnsureSuccessStatusCode();
        var restored = Assert.Single((await Home(client)).Favorites);
        Assert.Equal(file.Id, restored.Id);
        Assert.Equal(app.Clock.Now.UtcDateTime, restored.RecentAt);
        Assert.Empty(await Trash(client));
        using var download = await client.PostAsJsonAsync("/api/download", new { paths = new[] { "keep.txt" } });
        Assert.Equal("original bytes", await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FolderTrashIsAtomicAndReusingNamesNeverOverwritesTrashedBytes()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await SignIn(client);
        (await client.PostAsJsonAsync("/api/space/folders", new { parentPath = ".", name = "folder" })).EnsureSuccessStatusCode();
        await Upload(client, "file.txt", "old", "folder");
        var file = Assert.Single((await Home(client)).Recent);
        await Favorite(client, file.Id, true);
        (await client.DeleteAsync("/api/space/entry?path=folder")).EnsureSuccessStatusCode();
        var trash = Assert.Single(await Trash(client));
        Assert.True(trash.IsFolder);
        Assert.Empty((await Home(client)).Recent);
        (await client.PostAsJsonAsync("/api/space/folders", new { parentPath = ".", name = "folder" })).EnsureSuccessStatusCode();
        await Upload(client, "file.txt", "new", "folder");
        Assert.NotEqual(file.Id, Assert.Single((await Home(client)).Recent).Id);
        Assert.Empty((await Home(client)).Favorites);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/space/trash/{trash.Id}/restore", new { })).StatusCode);
        (await client.PostAsJsonAsync("/api/space/rename", new { path = "folder", name = "replacement" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/space/trash/{trash.Id}/restore", new { })).EnsureSuccessStatusCode();
        Assert.Equal(file.Id, Assert.Single((await Home(client)).Favorites).Id);
        using var oldDownload = await client.PostAsJsonAsync("/api/download", new { paths = new[] { "folder/file.txt" } });
        using var newDownload = await client.PostAsJsonAsync("/api/download", new { paths = new[] { "replacement/file.txt" } });
        Assert.Equal("old", await oldDownload.Content.ReadAsStringAsync());
        Assert.Equal("new", await newDownload.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RestoreRequiresParentFolderAndDoesNotOverwriteReplacementFile()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await SignIn(client);
        (await client.PostAsJsonAsync("/api/space/folders", new { parentPath = ".", name = "parent" })).EnsureSuccessStatusCode();
        await Upload(client, "file.txt", "old", "parent");
        (await client.DeleteAsync("/api/space/entry?path=parent/file.txt")).EnsureSuccessStatusCode();
        var file = Assert.Single(await Trash(client));
        (await client.DeleteAsync("/api/space/entry?path=parent")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/space/trash/{file.Id}/restore", new { })).StatusCode);
        var folder = (await Trash(client)).Single(t => t.IsFolder);
        (await client.PostAsJsonAsync($"/api/space/trash/{folder.Id}/restore", new { })).EnsureSuccessStatusCode();
        await Upload(client, "file.txt", "replacement", "parent");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/space/trash/{file.Id}/restore", new { })).StatusCode);
        using var download = await client.PostAsJsonAsync("/api/download", new { paths = new[] { "parent/file.txt" } });
        Assert.Equal("replacement", await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SevenDayBoundaryExpiresExactlyAndCleanupRemovesOnlyExpiredTrash()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client);
        await Upload(client, "old.txt", "old");
        (await client.DeleteAsync("/api/space/entry?path=old.txt")).EnsureSuccessStatusCode();
        var old = Assert.Single(await Trash(client));
        app.Clock.Now = app.Clock.Now.AddDays(1);
        await Upload(client, "new.txt", "new");
        (await client.DeleteAsync("/api/space/entry?path=new.txt")).EnsureSuccessStatusCode();
        await Upload(client, "live.txt", "live");
        app.Clock.Now = new DateTimeOffset(old.ExpiresAt).AddTicks(-1);
        await Cleanup(app);
        Assert.Equal(2, (await Trash(client)).Length);
        app.Clock.Now = new DateTimeOffset(old.ExpiresAt);
        Assert.Equal(HttpStatusCode.Gone, (await client.PostAsJsonAsync($"/api/space/trash/{old.Id}/restore", new { })).StatusCode);
        await Cleanup(app);
        await Cleanup(app);
        Assert.Equal("new.txt", Assert.Single(await Trash(client)).OriginalPath);
        Assert.Equal("live.txt", Assert.Single((await Home(client)).Recent).Name);
        using var scope = app.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<UserStorage>();
        Assert.False(File.Exists(storage.TrashPath(Principal(user), old.Id)));
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Files.AnyAsync(f => f.TrashEntryId == old.Id));
    }

    [Fact]
    public async Task RestoreJustBeforeDeadlinePreventsLaterCleanup()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await SignIn(client);
        await Upload(client, "restore.txt", "safe");
        (await client.DeleteAsync("/api/space/entry?path=restore.txt")).EnsureSuccessStatusCode();
        var item = Assert.Single(await Trash(client));
        app.Clock.Now = new DateTimeOffset(item.ExpiresAt).AddTicks(-1);
        (await client.PostAsJsonAsync($"/api/space/trash/{item.Id}/restore", new { })).EnsureSuccessStatusCode();
        app.Clock.Now = app.Clock.Now.AddDays(2);
        await Cleanup(app);
        Assert.Equal("restore.txt", Assert.Single((await Home(client)).Recent).Name);
    }

    [Fact]
    public async Task PermanentTrashDeletionFreesQuotaAndRemovesFileMetadata()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client, "alice");
        await Upload(client, "unused.txt", "four");
        (await client.DeleteAsync("/api/space/entry?path=unused.txt")).EnsureSuccessStatusCode();
        var item = Assert.Single(await Trash(client));
        Assert.Equal(4, (await client.GetFromJsonAsync<DriveStats>("/api/space/drive-stats"))!.OccupiedSpace);

        using var deleted = await client.DeleteAsync($"/api/space/trash/{item.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await Trash(client));
        Assert.Equal(0, (await client.GetFromJsonAsync<DriveStats>("/api/space/drive-stats"))!.OccupiedSpace);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/space/trash/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/space/trash/{item.Id}/restore", new { })).StatusCode);
        using var scope = app.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<UserStorage>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(File.Exists(storage.TrashPath(Principal(user), item.Id)));
        Assert.False(await db.TrashEntries.AnyAsync(entry => entry.Id == item.Id));
        Assert.False(await db.Files.AnyAsync(file => file.TrashEntryId == item.Id));
    }

    [Fact]
    public async Task PermanentTrashDeletionRemovesFoldersAndIsPrivateEvenFromAdmin()
    {
        await using var app = new TestApplication();
        using var alice = app.CreateClient();
        using var bob = app.CreateClient();
        using var admin = app.CreateClient();
        var user = await SignIn(alice, "alice");
        await SignIn(bob, "bob");
        await SignIn(admin);
        (await alice.PostAsJsonAsync("/api/space/folders", new { parentPath = ".", name = "folder" })).EnsureSuccessStatusCode();
        await Upload(alice, "nested.txt", "nested", "folder");
        (await alice.DeleteAsync("/api/space/entry?path=folder")).EnsureSuccessStatusCode();
        var item = Assert.Single(await Trash(alice));
        foreach (var other in new[] { bob, admin })
        {
            using var denied = await other.DeleteAsync($"/api/space/trash/{item.Id}");
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        using var scope = app.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<UserStorage>();
        Assert.True(File.Exists(Path.Combine(storage.TrashPath(Principal(user), item.Id), "nested.txt")));

        using var deleted = await alice.DeleteAsync($"/api/space/trash/{item.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(Directory.Exists(storage.TrashPath(Principal(user), item.Id)));
        Assert.Equal(0, (await alice.GetFromJsonAsync<DriveStats>("/api/space/drive-stats"))!.OccupiedSpace);
    }

    [Fact]
    public async Task FavoritesTrashAndRestoreArePrivateEvenFromAdministrator()
    {
        await using var app = new TestApplication();
        using var alice = app.CreateClient();
        using var bob = app.CreateClient();
        using var admin = app.CreateClient();
        await SignIn(alice, "alice");
        await SignIn(bob, "bob");
        await SignIn(admin);
        await Upload(alice, "private.txt", "private");
        var file = Assert.Single((await Home(alice)).Recent);
        await Favorite(alice, file.Id, true);
        foreach (var other in new[] { bob, admin })
        {
            Assert.Empty((await Home(other)).Favorites);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/space/files/{file.Id}/favorite", new { isFavorite = true })).StatusCode);
        }
        (await alice.DeleteAsync("/api/space/entry?path=private.txt")).EnsureSuccessStatusCode();
        var trash = Assert.Single(await Trash(alice));
        foreach (var other in new[] { bob, admin })
        {
            Assert.Empty(await Trash(other));
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/space/trash/{trash.Id}/restore", new { })).StatusCode);
        }
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("alice")]
    public async Task PrivateTrashCannotBeAccessedByPathOrIncludedInRootDownload(string username)
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await SignIn(client, username);
        await Upload(client, "secret.txt", "secret bytes");
        (await client.DeleteAsync("/api/space/entry?path=secret.txt")).EnsureSuccessStatusCode();
        var item = Assert.Single(await Trash(client));
        var path = UserStorage.TrashDirectory + "/" + item.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/download", new { paths = new[] { path } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/space/folder-info?relativePath=" + UserStorage.TrashDirectory)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/space/entry?path=" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/space/folders", new { parentPath = ".", name = UserStorage.TrashDirectory })).StatusCode);
        using var download = await client.PostAsJsonAsync("/api/download", new { paths = new[] { "." } });
        download.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(await download.Content.ReadAsStreamAsync());
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains(UserStorage.TrashDirectory) || e.FullName.Contains("secret"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedTrashMoveRecoversBeforeSubsequentFileOperations(bool bytesAlreadyMoved)
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client);
        await Upload(client, "recover.txt", "recoverable");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<UserStorage>();
            var entry = new TrashEntry { UserId = user.User.Id, OriginalPath = "recover.txt", TrashedAt = app.Clock.Now.UtcDateTime,
                ExpiresAt = app.Clock.Now.UtcDateTime.AddDays(7), Size = 11 };
            db.TrashEntries.Add(entry);
            (await db.Files.SingleAsync()).TrashEntryId = entry.Id;
            await db.SaveChangesAsync();
            if (bytesAlreadyMoved)
            {
                var target = storage.TrashPath(Principal(user), entry.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(storage.Resolve(Principal(user), "recover.txt"), target);
            }
        }
        Assert.Empty((await Home(client)).Recent);
        var item = Assert.Single(await Trash(client));
        (await client.PostAsJsonAsync($"/api/space/trash/{item.Id}/restore", new { })).EnsureSuccessStatusCode();
        Assert.Equal("recover.txt", Assert.Single((await Home(client)).Recent).Name);
    }

    [Fact]
    public async Task InterruptedRestoreRecoversMetadataAfterBytesHaveMoved()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client);
        await Upload(client, "restore.txt", "recoverable");
        var file = Assert.Single((await Home(client)).Recent);
        await Favorite(client, file.Id, true);
        (await client.DeleteAsync("/api/space/entry?path=restore.txt")).EnsureSuccessStatusCode();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<UserStorage>();
            var entry = await db.TrashEntries.SingleAsync();
            entry.State = TrashState.Restoring;
            await db.SaveChangesAsync();
            File.Move(storage.TrashPath(Principal(user), entry.Id), storage.Resolve(Principal(user), entry.OriginalPath));
        }
        Assert.Equal(file.Id, Assert.Single((await Home(client)).Favorites).Id);
        Assert.Empty(await Trash(client));
    }

    [Fact]
    public async Task FailedPurgeRetainsRecordAndRetriesSuccessfully()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await SignIn(client);
        await Upload(client, "locked.txt", "retry");
        (await client.DeleteAsync("/api/space/entry?path=locked.txt")).EnsureSuccessStatusCode();
        var item = Assert.Single(await Trash(client));
        using var scope = app.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<UserStorage>();
        var path = storage.TrashPath(Principal(user), item.Id);
        app.Clock.Now = new DateTimeOffset(item.ExpiresAt);
        // An open file without delete sharing exercises a retryable Windows storage failure.
        if (OperatingSystem.IsWindows())
        {
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Cleanup(app);
                Assert.True(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TrashEntries.AnyAsync(t => t.Id == item.Id));
            }
        }
        await Cleanup(app);
        Assert.False(File.Exists(path));
        Assert.Empty(await Trash(client));
    }

    private static async Task<LoginResponse> SignIn(HttpClient client, string username = "admin")
    {
        var response = await client.PostAsJsonAsync(username == "admin" ? "/api/auth/login" : "/api/auth/signup",
            new { username, password = username == "admin" ? "admin@123" : "account@123" });
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", result.AccessToken);
        return result;
    }

    private static ClaimsPrincipal Principal(LoginResponse login)
    {
        var identity = new ClaimsIdentity("test", "name", "role");
        identity.AddClaim(new Claim("sub", login.User.Id));
        foreach (var role in login.User.Roles) identity.AddClaim(new Claim("role", role));
        return new ClaimsPrincipal(identity);
    }

    private static async Task Upload(HttpClient client, string name, string contents, string folder = ".")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(folder), "DestinationPath");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(contents)), "Files", name);
        (await client.PostAsync("/api/upload", form)).EnsureSuccessStatusCode();
    }

    private static async Task Favorite(HttpClient client, string id, bool isFavorite) =>
        (await client.PutAsJsonAsync($"/api/space/files/{id}/favorite", new { isFavorite })).EnsureSuccessStatusCode();
    private static async Task<HomeFiles> Home(HttpClient client) => (await client.GetFromJsonAsync<HomeFiles>("/api/space/home"))!;
    private static async Task<TrashEntry[]> Trash(HttpClient client) => (await client.GetFromJsonAsync<TrashEntry[]>("/api/space/trash"))!;
    private static async Task Cleanup(TestApplication app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TrashCleanup>().RunAsync();
    }
}
