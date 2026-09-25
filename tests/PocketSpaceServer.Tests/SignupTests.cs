using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Tests;

public class SignupTests
{
    [Fact]
    public async Task SignupCreatesPendingUserPrivateFolderAndSevenDayDeadline()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var signup = await Signup(client, "alice");
        Assert.Equal(AccountStatus.Pending, signup.User.Status);
        Assert.Equal(new[] { "User" }, signup.User.Roles);
        Assert.Equal(app.Clock.Now.UtcDateTime.AddDays(7), signup.User.PendingExpiresAt);
        Assert.Null(signup.User.ApprovedAt);
        using var scope = app.Services.CreateScope();
        var root = scope.ServiceProvider.GetRequiredService<UserStorage>().UserRoot(signup.User.Id);
        Assert.True(Directory.Exists(root));
        await Upload(client, "hello.txt", "private content");
        Assert.Equal("private content", await File.ReadAllTextAsync(Path.Combine(root, "hello.txt")));
        var me = await client.GetFromJsonAsync<CurrentUser>("/api/auth/me");
        Assert.Equal(DateTimeKind.Utc, me!.PendingExpiresAt!.Value.Kind);
        using var duplicate = await client.PostAsJsonAsync("/api/auth/signup", new { username = "ALICE", password = "alice@123" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task PendingUsersCanManageFilesButCannotReadOrChangeOtherUsersFiles()
    {
        await using var app = new TestApplication();
        using var alice = app.CreateClient();
        using var bob = app.CreateClient();
        var a = await Signup(alice, "alice");
        var b = await Signup(bob, "bob");
        await Upload(bob, "secret.txt", "bob private");
        using var listing = await alice.GetAsync("/api/space/folder-info");
        Assert.DoesNotContain("secret.txt", await listing.Content.ReadAsStringAsync());
        using var missing = await alice.PostAsJsonAsync("/api/download", new { paths = new[] { "secret.txt" } });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var escape = $"../{b.User.Id}/secret.txt";
        using var read = await alice.PostAsJsonAsync("/api/download", new { paths = new[] { escape } });
        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        using var deleteOther = await alice.DeleteAsync("/api/space/entry?path=" + Uri.EscapeDataString(escape));
        Assert.Equal(HttpStatusCode.BadRequest, deleteOther.StatusCode);
        using var create = await alice.PostAsJsonAsync("/api/space/folders", new { parentPath = ".", name = "notes" });
        Assert.Equal(HttpStatusCode.NoContent, create.StatusCode);
        await Upload(alice, "one.txt", "alice private", "notes");
        using var rename = await alice.PostAsJsonAsync("/api/space/rename", new { path = "notes/one.txt", name = "two.txt" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        using var zip = await alice.PostAsJsonAsync("/api/download", new { paths = new[] { "notes" } });
        zip.EnsureSuccessStatusCode();
        using var archive = new ZipArchive(await zip.Content.ReadAsStreamAsync());
        Assert.Equal(new[] { "notes/", "notes/two.txt" }, archive.Entries.Select(e => e.FullName));
        using var remove = await alice.DeleteAsync("/api/space/entry?path=notes");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        using var rootDelete = await alice.DeleteAsync("/api/space/entry?path=.");
        Assert.Equal(HttpStatusCode.BadRequest, rootDelete.StatusCode);
        using var bobDownload = await bob.PostAsJsonAsync("/api/download", new { paths = new[] { "secret.txt" } });
        Assert.Equal("bob private", await bobDownload.Content.ReadAsStringAsync());
        using var approveSelf = await alice.PostAsJsonAsync($"/api/admin/users/{a.User.Id}/approve", new { });
        Assert.Equal(HttpStatusCode.Forbidden, approveSelf.StatusCode);
        using var pendingList = await alice.GetAsync("/api/admin/users/pending");
        Assert.Equal(HttpStatusCode.Forbidden, pendingList.StatusCode);
    }

    [Fact]
    public async Task ApprovalPreservesFolderAndAccountBeyondSevenDays()
    {
        await using var app = new TestApplication();
        using var user = app.CreateClient();
        var signup = await Signup(user, "approved");
        await Upload(user, "keep.txt", "keep me");
        using var admin = await Admin(app);
        using var pending = await admin.GetAsync("/api/admin/users/pending");
        Assert.Contains("approved", await pending.Content.ReadAsStringAsync());
        using var approval = await admin.PostAsJsonAsync($"/api/admin/users/{signup.User.Id}/approve", new { });
        Assert.Equal(HttpStatusCode.NoContent, approval.StatusCode);
        var me = await user.GetFromJsonAsync<CurrentUser>("/api/auth/me");
        Assert.Equal(AccountStatus.Approved, me!.Status);
        Assert.Null(me.PendingExpiresAt);
        app.Clock.Now = app.Clock.Now.AddDays(8);
        await Cleanup(app);
        using var scope = app.Services.CreateScope();
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.FindAsync(signup.User.Id));
        var root = scope.ServiceProvider.GetRequiredService<UserStorage>().UserRoot(signup.User.Id);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(root, "keep.txt")));
    }

    [Fact]
    public async Task ExpiryDeniesTokensThenDeletesOnlyExpiredAccountAndItsFiles()
    {
        await using var app = new TestApplication();
        using var expired = app.CreateClient();
        var signup = await Signup(expired, "expired");
        await Upload(expired, "delete.txt", "delete me");
        using var retained = app.CreateClient();
        var later = await Signup(retained, "retained");
        using var admin = await Admin(app);
        using var approved = await admin.PostAsJsonAsync($"/api/admin/users/{later.User.Id}/approve", new { });
        approved.EnsureSuccessStatusCode();
        await Upload(retained, "keep.txt", "retained data");
        using var scope = app.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<UserStorage>();
        var root = storage.UserRoot(signup.User.Id);
        app.Clock.Now = app.Clock.Now.AddDays(7).AddTicks(-1);
        await Cleanup(app);
        Assert.True(Directory.Exists(root));
        app.Clock.Now = app.Clock.Now.AddTicks(1);
        using var rejected = await expired.GetAsync("/api/space/folder-info");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var login = await expired.PostAsJsonAsync("/api/auth/login", new { username = "expired", password = "account@123" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        using var tooLate = await admin.PostAsJsonAsync($"/api/admin/users/{signup.User.Id}/approve", new { });
        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
        await Cleanup(app);
        await Cleanup(app); // Safe when repeated after deletion.
        Assert.False(Directory.Exists(root));
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Null(await db.Users.FindAsync(signup.User.Id));
        Assert.False(await db.UserRoles.AnyAsync(role => role.UserId == signup.User.Id));
        Assert.True(await db.Users.AnyAsync(u => u.UserName == "admin"));
        Assert.Equal("retained data", await File.ReadAllTextAsync(Path.Combine(storage.UserRoot(later.User.Id), "keep.txt")));
    }

    [Fact]
    public async Task FailedDeletionKeepsTombstoneAndRetriesWithoutAllowingLogin()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var signup = await Signup(client, "retry");
        using var scope = app.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<UserStorage>();
        var root = storage.UserRoot(signup.User.Id);
        await File.WriteAllTextAsync(Path.Combine(root, "locked.txt"), "test");
        app.Clock.Now = app.Clock.Now.AddDays(8);
        if (OperatingSystem.IsWindows())
        {
            using (var locked = new FileStream(Path.Combine(root, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Cleanup(app);
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(AccountStatus.Deleting, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == signup.User.Id)).Status);
            }
        }
        await Cleanup(app);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("../../admin")]
    [InlineData("a")]
    [InlineData("space name")]
    public async Task InvalidSignupDoesNotCreateAccount(string username)
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/signup", new { username, password = "account@123" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = app.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.CountAsync());
    }

    [Theory]
    [InlineData("../another-user")]
    [InlineData("/absolute/path")]
    [InlineData("C:\\private")]
    [InlineData("folder/file:stream")]
    [InlineData("folder. /file")]
    public async Task UnsafeStoragePathsAreRejected(string path)
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await Signup(client, "pathcheck");
        using var read = await client.GetAsync("/api/space/folder-info?relativePath=" + Uri.EscapeDataString(path));
        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        using var create = await client.PostAsJsonAsync("/api/space/folders", new { parentPath = path, name = "new" });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task CleanupWaitsForActiveStorageOperationThenRemovesTheAccount()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var user = await Signup(client, "activefile");
        using var scope = app.Services.CreateScope();
        var root = scope.ServiceProvider.GetRequiredService<UserStorage>().UserRoot(user.User.Id);
        app.Clock.Now = app.Clock.Now.AddDays(8);
        var lease = await app.Services.GetRequiredService<AccountOperationLocks>().AcquireAsync(user.User.Id, CancellationToken.None);
        Task cleanup;
        try
        {
            cleanup = Cleanup(app);
            Assert.False(cleanup.IsCompleted);
            Assert.True(Directory.Exists(root));
        }
        finally { lease.Dispose(); }
        await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(root));
    }

    private static async Task<LoginResponse> Signup(HttpClient client, string username)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/signup", new { username, password = "account@123", role = "Admin", status = "Approved" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", result.AccessToken);
        return result;
    }

    private static async Task Upload(HttpClient client, string name, string contents, string folder = ".")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(folder), "DestinationPath");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(contents)), "Files", name);
        using var response = await client.PostAsync("/api/upload", form);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpClient> Admin(TestApplication app)
    {
        var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin@123" });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
        return client;
    }

    private static async Task Cleanup(TestApplication app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PendingAccountCleanup>().RunAsync();
    }
}
