using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Tests;

public class QuotaTests
{
    [Fact]
    public async Task DefaultQuotaIsVisibleAndOnlyAdminCanIncreaseIt()
    {
        await using var app = new TestApplication();
        using var alice = app.CreateClient();
        var account = await Signup(alice);
        var stats = (await alice.GetFromJsonAsync<DriveStats>("/api/space/drive-stats"))!;
        Assert.Equal(UserQuota.DefaultBytes, stats.QuotaBytes);
        Assert.Equal(0, stats.OccupiedSpace);

        using var denied = await alice.PutAsJsonAsync($"/api/admin/users/{account.User.Id}/quota",
            new { quotaBytes = 600 * UserQuota.Megabyte });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var admin = await Admin(app);
        var accounts = (await admin.GetFromJsonAsync<QuotaAccount[]>("/api/admin/users"))!;
        Assert.Contains(accounts, row => row.Id == account.User.Id && row.QuotaBytes == UserQuota.DefaultBytes);
        using var same = await admin.PutAsJsonAsync($"/api/admin/users/{account.User.Id}/quota",
            new { quotaBytes = UserQuota.DefaultBytes });
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        using var changed = await admin.PutAsJsonAsync($"/api/admin/users/{account.User.Id}/quota",
            new { quotaBytes = 600 * UserQuota.Megabyte });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(600 * UserQuota.Megabyte,
            (await alice.GetFromJsonAsync<DriveStats>("/api/space/drive-stats"))!.QuotaBytes);
    }

    [Fact]
    public async Task UploadCountsReplacementsAndTrashBeforeWriting()
    {
        await using var app = new TestApplication();
        using var alice = app.CreateClient();
        var account = await Signup(alice);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Users.Where(user => user.Id == account.User.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.QuotaBytes, 5L));
        }

        using var accepted = await Upload(alice, "file.txt", "four");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("a"u8.ToArray()), "Files", "one.txt");
            form.Add(new ByteArrayContent("b"u8.ToArray()), "Files", "two.txt");
            using var rejectedBatch = await alice.PostAsync("/api/upload", form);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejectedBatch.StatusCode);
        }
        using var rejectedReplacement = await Upload(alice, "file.txt", "too long");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejectedReplacement.StatusCode);
        using var downloaded = await alice.PostAsJsonAsync("/api/download", new { paths = new[] { "file.txt" } });
        Assert.Equal("four", await downloaded.Content.ReadAsStringAsync());
        Assert.Equal(4, (await alice.GetFromJsonAsync<DriveStats>("/api/space/drive-stats"))!.OccupiedSpace);

        using var trashed = await alice.DeleteAsync("/api/space/entry?path=file.txt");
        Assert.Equal(HttpStatusCode.NoContent, trashed.StatusCode);
        Assert.Equal(4, (await alice.GetFromJsonAsync<DriveStats>("/api/space/drive-stats"))!.OccupiedSpace);
        using var rejectedNewFile = await Upload(alice, "new.txt", "two");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejectedNewFile.StatusCode);
        using var missing = await alice.GetAsync("/api/space/folder-info?relativePath=.");
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Empty((await missing.Content.ReadFromJsonAsync<FolderInfo>())!.Files);

        using var admin = await Admin(app);
        using var increased = await admin.PutAsJsonAsync($"/api/admin/users/{account.User.Id}/quota",
            new { quotaBytes = 600 * UserQuota.Megabyte });
        Assert.Equal(HttpStatusCode.NoContent, increased.StatusCode);
        using var retried = await Upload(alice, "new.txt", "two");
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(7, (await alice.GetFromJsonAsync<DriveStats>("/api/space/drive-stats"))!.OccupiedSpace);
    }

    private static async Task<LoginResponse> Signup(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/signup",
            new { username = "alice", password = "account@123" });
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        return login;
    }

    private static async Task<HttpClient> Admin(TestApplication app)
    {
        var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin@123" });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
        return client;
    }

    private static Task<HttpResponseMessage> Upload(HttpClient client, string name, string contents)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(contents)), "Files", name);
        return client.PostAsync("/api/upload", form);
    }

    private sealed record QuotaAccount(string Id, long QuotaBytes);
}
