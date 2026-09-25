using System.Net.WebSockets;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(
    builder.Configuration.GetConnectionString("PocketSpace")
    ?? $"Data Source={Path.Combine(dataDirectory, "pocketspace.db")}"));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireUppercase = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
}).AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>().AddDefaultTokenProviders();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AccountOperationLocks>();
builder.Services.AddScoped<UserStorage>();
builder.Services.AddScoped<FileCatalog>();
builder.Services.AddScoped<TrashCleanup>();
builder.Services.AddHostedService<TrashCleanupWorker>();
builder.Services.AddScoped<PendingAccountCleanup>();
builder.Services.AddHostedService<PendingAccountCleanupWorker>();

var jwt = JwtSettings.Load(builder.Configuration, builder.Environment, dataDirectory);
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer,
        ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = jwt.Key,
        ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
        ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
        NameClaimType = "name", RoleClaimType = "role"
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
            var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
            var id = context.Principal?.FindFirst("sub")?.Value;
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, context.HttpContext.RequestAborted);
            if (user is null || !user.CanAccess(clock.GetUtcNow().UtcDateTime) ||
                context.Principal?.FindFirst("security_stamp")?.Value != user.SecurityStamp)
                context.Fail("Account is unavailable.");
        },
        // Browser WebSocket clients cannot set the Authorization header. Pass the
        // token as a subprotocol so credentials never appear in URL/access logs.
        OnMessageReceived = context =>
        {
            if (context.Request.Path == "/ws" && context.HttpContext.WebSockets.IsWebSocketRequest)
            {
                var protocol = context.HttpContext.WebSockets.WebSocketRequestedProtocols
                    .FirstOrDefault(p => p.StartsWith("bearer.", StringComparison.Ordinal));
                if (protocol is not null) context.Token = protocol[7..];
            }
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});

builder.Services.AddRouting(options => options.LowercaseUrls = true);
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowViteFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "https://localhost:5173") // Vite dev server
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("Content-Disposition");
    });
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.AllowSynchronousIO = true;
    //options.Limits.MaxRequestBodySize
});

builder.Services.Configure<DirectorySettings>(
    builder.Configuration.GetSection("PocketSpace:DirectorySettings"));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference
            { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>()
    });
});

var app = builder.Build();
await DatabaseInitializer.InitializeAsync(app.Services, app.Configuration);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowViteFrontend");
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<AccountStorageMiddleware>();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync(
        context.WebSockets.WebSocketRequestedProtocols.Contains("pocketspace") ? "pocketspace" : null);
    using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var expiresAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(context.User.FindFirst("exp")!.Value));
    var remaining = expiresAt - DateTimeOffset.UtcNow;
    if (remaining <= TimeSpan.Zero) return;
    connectionLifetime.CancelAfter(remaining);
    var stateMessage = Encoding.UTF8.GetBytes("server-state:Idle");
    await socket.SendAsync(
        stateMessage,
        WebSocketMessageType.Text,
        endOfMessage: true,
        connectionLifetime.Token);

    var buffer = new byte[1024];
    try
    {
        while (socket.State == WebSocketState.Open &&
               !connectionLifetime.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, connectionLifetime.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client closed the connection.",
                    CancellationToken.None);
            }
        }
    }
    catch (OperationCanceledException)
    {
        // The browser disconnected or the development server is stopping.
    }
    catch (WebSocketException)
    {
        // The client disconnected without completing the close handshake.
    }
}).RequireAuthorization();

// Ensure TargetDirectory exists
using (var scope = app.Services.CreateScope())
{
    var config = scope.ServiceProvider.GetRequiredService<IOptions<DirectorySettings>>().Value;
    if (!Directory.Exists(config.TargetDirectory))
    {
        Directory.CreateDirectory(config.TargetDirectory);
        Console.WriteLine($"Created directory: {config.TargetDirectory}");
    }
}

// Vite proxies /api to the local HTTP endpoint during development. Production
// traffic should still be redirected to HTTPS by the application or its proxy.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.MapControllers().RequireAuthorization();

app.Run();

public partial class Program { }
