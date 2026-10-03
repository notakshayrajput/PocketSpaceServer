using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PocketSpaceServer.Authentication;

namespace PocketSpaceServer.Tests;

public class AccountStorageConcurrencyTests
{
    [Fact]
    public async Task FolderListingDoesNotWaitForAnActiveUploadLock()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        using var loginResponse = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin@123" });
        loginResponse.EnsureSuccessStatusCode();
        var login = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);

        var operations = app.Services.GetRequiredService<AccountOperationLocks>();
        using var uploadLease = await operations.AcquireAsync(login.User.Id, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listing = await client.GetAsync("/api/space/folder-info?relativePath=.", timeout.Token);

        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
    }
}
