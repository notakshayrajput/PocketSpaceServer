using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Tests;

public class PasswordTests
{
    [Fact]
    public async Task RequestIsGenericAndRepeatedRequestsKeepOriginalTimestamp()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await Signup(client);
        using var known = await Request(client, " ALICE ");
        using var unknown = await Request(client, "unknown");
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        var first = app.Clock.Now.UtcDateTime;
        using var admin = await Admin(app);
        app.Clock.Now = app.Clock.Now.AddMinutes(1);
        using var repeated = await Request(client, "alice");
        repeated.EnsureSuccessStatusCode();
        var requests = await admin.GetFromJsonAsync<ResetRow[]>("/api/admin/users/password-reset-requests");
        var row = Assert.Single(requests!);
        Assert.Equal("alice", row.Username);
        Assert.Equal(first, row.PasswordResetRequestedAt);
        Assert.Equal(DateTimeKind.Utc, row.PasswordResetRequestedAt.Kind);
    }

    [Fact]
    public async Task AdminResetClearsRequestAndLockoutAndRevokesOldPasswordAndToken()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var signup = await Signup(client);
        using var request = await Request(client, "alice");
        for (var i = 0; i < 5; i++)
        {
            using var failed = await Login(client, "alice", "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }
        using var admin = await Admin(app);
        using var reset = await Reset(admin, signup.User.Id, "temporary@123");
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<ResetRow[]>("/api/admin/users/password-reset-requests"))!);
        using var oldToken = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        using var oldPassword = await Login(client, "alice", "original@123");
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        using var login = await Login(client, "alice", "temporary@123");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.Equal(signup.User.PendingExpiresAt, session.User.PendingExpiresAt);
        client.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);
        using var change = await Change(client, "temporary@123", "personal@456");
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        using var revoked = await client.GetAsync("/api/space/folder-info");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var finalLogin = await Login(client, "alice", "personal@456");
        Assert.Equal(HttpStatusCode.OK, finalLogin.StatusCode);
        using var scope = app.Services.CreateScope();
        var user = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync("alice"))!;
        Assert.NotEqual("personal@456", user.PasswordHash);
        Assert.Null(user.PasswordResetRequestedAt);
        Assert.Null(user.LockoutEnd);
    }

    [Theory]
    [InlineData("wrong@123", "valid@456")]
    [InlineData("original@123", "abcdefgh")]
    [InlineData("original@123", "short")]
    [InlineData("original@123", "original@123")]
    public async Task InvalidChangePreservesPasswordTokenAndRequest(string current, string next)
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        await Signup(client);
        using var request = await Request(client, "alice");
        using var change = await Change(client, current, next);
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var login = await Login(client, "alice", "original@123");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var admin = await Admin(app);
        Assert.Single((await admin.GetFromJsonAsync<ResetRow[]>("/api/admin/users/password-reset-requests"))!);
    }

    [Fact]
    public async Task ResetAndQueueAreAdminOnlyAndChangeRequiresLogin()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        using var anonymousChange = await Change(client, "original@123", "next@123");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousChange.StatusCode);
        using var anonymousQueue = await client.GetAsync("/api/admin/users/password-reset-requests");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousQueue.StatusCode);
        var signup = await Signup(client);
        using var request = await Request(client, "alice");
        using var denied = await Reset(client, signup.User.Id, "next@123");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var queue = await client.GetAsync("/api/admin/users/password-reset-requests");
        Assert.Equal(HttpStatusCode.Forbidden, queue.StatusCode);
    }

    [Fact]
    public async Task InvalidResetPreservesRequestAndMissingOrHandledRequestsCannotBeReset()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var signup = await Signup(client);
        using var admin = await Admin(app);
        using var noRequest = await Reset(admin, signup.User.Id, "next@123");
        Assert.Equal(HttpStatusCode.Conflict, noRequest.StatusCode);
        using var missing = await Reset(admin, "missing", "next@123");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var request = await Request(client, "alice");
        using var invalid = await Reset(admin, signup.User.Id, "abcdefgh");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Single((await admin.GetFromJsonAsync<ResetRow[]>("/api/admin/users/password-reset-requests"))!);
        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var change = await Change(client, "original@123", "personal@456");
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        using var handled = await Reset(admin, signup.User.Id, "next@123");
        Assert.Equal(HttpStatusCode.Conflict, handled.StatusCode);
    }

    [Fact]
    public async Task ExpiredAccountsCannotRequestOrReceiveAReset()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var signup = await Signup(client);
        using var admin = await Admin(app);
        using var request = await Request(client, "alice");
        app.Clock.Now = app.Clock.Now.AddDays(7);
        using var expired = await Request(client, "alice");
        Assert.Equal(HttpStatusCode.OK, expired.StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<ResetRow[]>("/api/admin/users/password-reset-requests"))!);
        using var reset = await Reset(admin, signup.User.Id, "next@123");
        Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);
    }

    [Fact]
    public async Task ResetRequestsAreRateLimited()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        for (var i = 0; i < 20; i++)
        {
            using var response = await Request(client, "unknown");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var limited = await Request(client, "unknown");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private static async Task<LoginResponse> Signup(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/signup", new { username = "alice", password = "original@123" });
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);
        return session;
    }

    private static async Task<HttpClient> Admin(TestApplication app)
    {
        var client = app.CreateClient();
        using var response = await Login(client, "admin", "admin@123");
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
        return client;
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string username, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { username, password });
    private static Task<HttpResponseMessage> Request(HttpClient client, string username) =>
        client.PostAsJsonAsync("/api/auth/password-reset-request", new { username });
    private static Task<HttpResponseMessage> Reset(HttpClient client, string id, string newPassword) =>
        client.PostAsJsonAsync($"/api/admin/users/{id}/reset-password", new { newPassword });
    private static Task<HttpResponseMessage> Change(HttpClient client, string currentPassword, string newPassword) =>
        client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword, newPassword });
    private sealed record ResetRow(string Id, string Username, DateTime PasswordResetRequestedAt);
}
