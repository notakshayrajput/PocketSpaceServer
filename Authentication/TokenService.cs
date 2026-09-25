using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Authentication;

public sealed class TokenService(JwtSettings settings, TimeProvider clock)
{
    public LoginResponse Create(ApplicationUser user, IList<string> roles)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(settings.LifetimeMinutes);
        if (user.Status == AccountStatus.Pending && user.PendingExpiresAt < expires)
            expires = user.PendingExpiresAt.Value;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("security_stamp", user.SecurityStamp!),
            new("name", user.UserName!)
        };
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience, claims,
            now, expires, new SigningCredentials(settings.Key, SecurityAlgorithms.HmacSha256));
        return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), expires,
            CurrentUser.From(user, roles));
    }
}

public sealed record CurrentUser(string Id, string Username, IList<string> Roles, string Status,
    DateTime CreatedAt, DateTime? PendingExpiresAt, DateTime? ApprovedAt)
{
    public static CurrentUser From(ApplicationUser user, IList<string> roles) =>
        new(user.Id, user.UserName!, roles, user.Status, user.CreatedAt, user.PendingExpiresAt, user.ApprovedAt);
}
public sealed record LoginResponse(string AccessToken, DateTime ExpiresAt, CurrentUser User);
