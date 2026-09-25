using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace PocketSpaceServer.Authentication;

public sealed class JwtSettings
{
    public string Issuer { get; init; } = "PocketSpace";
    public string Audience { get; init; } = "PocketSpaceClient";
    public int LifetimeMinutes { get; init; } = 60;
    public required SymmetricSecurityKey Key { get; init; }

    public static JwtSettings Load(IConfiguration configuration, IHostEnvironment environment, string dataDirectory)
    {
        var signingKey = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Set Jwt:SigningKey to a random secret of at least 32 bytes outside Development.");

            var keyPath = Path.Combine(dataDirectory, "jwt-signing-key");
            if (!File.Exists(keyPath))
                File.WriteAllText(keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
            signingKey = File.ReadAllText(keyPath).Trim();
        }

        var bytes = Encoding.UTF8.GetBytes(signingKey);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes.");

        var lifetime = configuration.GetValue("Jwt:LifetimeMinutes", 60);
        if (lifetime is < 1 or > 1440)
            throw new InvalidOperationException("Jwt:LifetimeMinutes must be between 1 and 1440.");

        return new JwtSettings
        {
            Issuer = configuration["Jwt:Issuer"] ?? "PocketSpace",
            Audience = configuration["Jwt:Audience"] ?? "PocketSpaceClient",
            LifetimeMinutes = lifetime,
            Key = new SymmetricSecurityKey(bytes)
        };
    }
}
