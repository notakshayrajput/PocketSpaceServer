using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;
using PocketSpaceServer.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

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
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowViteFrontend");
app.UseWebSockets();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var stateMessage = Encoding.UTF8.GetBytes("server-state:Idle");
    await socket.SendAsync(
        stateMessage,
        WebSocketMessageType.Text,
        endOfMessage: true,
        context.RequestAborted);

    var buffer = new byte[1024];
    try
    {
        while (socket.State == WebSocketState.Open &&
               !context.RequestAborted.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
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
});

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

app.UseAuthorization();

app.MapControllers();

app.Run();
