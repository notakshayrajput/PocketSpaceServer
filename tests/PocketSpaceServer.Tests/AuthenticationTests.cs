using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Tests;

public class AuthenticationTests
{
    [Theory]
    [InlineData("GET", "/api/space/folder-info")]
    [InlineData("GET", "/api/space/drive-stats")]
    [InlineData("POST", "/api/upload")]
    [InlineData("POST", "/api/download")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("GET", "/weatherforecast")]
    [InlineData("GET", "/ws")]
    public async Task AllExistingEndpointsRequireLogin(string method, string path)
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DefaultAdminIsHashedAndSeedDoesNotResetExistingCredentials()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var login = await Login(client);
        Assert.Equal("admin", login.User.Username);
        Assert.Contains("Admin", login.User.Roles);
        Assert.InRange((login.ExpiresAt - DateTime.UtcNow).TotalMinutes, 59, 61);
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = (await users.FindByNameAsync("admin"))!;
        Assert.NotEqual("admin@123", admin.PasswordHash);
        var changed = await users.ChangePasswordAsync(admin, "admin@123", "changed@123");
        Assert.True(changed.Succeeded);
        await DatabaseInitializer.InitializeAsync(app.Services, app.Services.GetRequiredService<IConfiguration>());
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        using var wrong = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin@123" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var changedLogin = await client.PostAsJsonAsync("/api/auth/login", new { username = "ADMIN", password = "changed@123" });
        Assert.Equal(HttpStatusCode.OK, changedLogin.StatusCode);
    }

    [Fact]
    public async Task LoginRejectsUnknownInvalidAndLockedAccounts()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        using var unknown = await client.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var genericError = await unknown.Content.ReadAsStringAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal(genericError, await wrong.Content.ReadAsStringAsync());
        }
        using var locked = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin@123" });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("malformed")]
    public async Task InvalidTokensCannotReadFiles(string failure)
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var settings = app.Services.GetRequiredService<JwtSettings>();
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            failure == "issuer" ? "wrong" : settings.Issuer,
            failure == "audience" ? "wrong" : settings.Audience,
            new[] { new Claim("sub", "test-user") }, now.AddHours(-2),
            failure == "expired" ? now.AddMinutes(-1) : now.AddMinutes(1),
            new SigningCredentials(failure == "signature"
                ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('x', 64))) : settings.Key,
                SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            failure == "malformed" ? "invalid-token" : new JwtSecurityTokenHandler().WriteToken(token));
        using var response = await client.GetAsync("/api/space/folder-info");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminCanStillUploadAndDownloadExistingStorage()
    {
        await using var app = new TestApplication();
        using var adminClient = app.CreateClient();
        var adminLogin = await Login(adminClient);
        adminClient.DefaultRequestHeaders.Authorization = new("Bearer", adminLogin.AccessToken);
        var current = await adminClient.GetFromJsonAsync<CurrentUser>("/api/auth/me");
        Assert.Equal(adminLogin.User.Id, current!.Id);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("."), "DestinationPath");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("shared contents")), "Files", "shared.txt");
        using var upload = await adminClient.PostAsync("/api/upload", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        using var listing = await adminClient.GetAsync("/api/space/folder-info");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        Assert.Contains("shared.txt", await listing.Content.ReadAsStringAsync());
        using var download = await adminClient.PostAsJsonAsync("/api/download", new { paths = new[] { "shared.txt" } });
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("shared contents", await download.Content.ReadAsStringAsync());
        using var stats = await adminClient.GetAsync("/api/space/drive-stats");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
    }

    [Fact]
    public async Task WebSocketStatusRequiresTokenAndAcceptsBrowserProtocol()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();
        var anonymous = app.Server.CreateWebSocketClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => anonymous.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None));
        var login = await Login(client);
        var authenticated = app.Server.CreateWebSocketClient();
        authenticated.SubProtocols.Add("pocketspace");
        authenticated.SubProtocols.Add("bearer." + login.AccessToken);
        using var socket = await authenticated.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
        Assert.Equal("pocketspace", socket.SubProtocol);
        var buffer = new byte[128];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var message = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal("server-state:Idle", Encoding.UTF8.GetString(buffer, 0, message.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test complete", timeout.Token);
    }

    private static async Task<LoginResponse> Login(HttpClient client, string username = "admin", string password = "admin@123")
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }
}

internal sealed class TestApplication : WebApplicationFactory<Program>
{
    public TestClock Clock { get; } = new();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PocketSpaceTests", Guid.NewGuid().ToString("N"));

    public TestApplication() => Directory.CreateDirectory(directory);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
        builder.UseSetting("ConnectionStrings:PocketSpace", $"Data Source={Path.Combine(directory, "test.db")}");
        builder.UseSetting("Jwt:SigningKey", "test-only-signing-key-which-is-at-least-32-bytes-long");
        builder.UseSetting("PocketSpace:DirectorySettings:TargetDirectory", Path.Combine(directory, "files"));
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
