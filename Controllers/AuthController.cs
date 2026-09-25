using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AuthController(UserManager<ApplicationUser> users, TokenService tokens, ApplicationDbContext db,
    UserStorage storage, TimeProvider clock, AccountOperationLocks operations) : ControllerBase
{
    [HttpPost("signup")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Signup(SignupRequest request)
    {
        var username = request.Username.Trim();
        if (await users.FindByNameAsync(username) is not null)
            return Conflict(new { message = "That username is already taken." });

        var now = clock.GetUtcNow().UtcDateTime;
        var user = new ApplicationUser
        {
            UserName = username, CreatedAt = now, Status = AccountStatus.Pending,
            PendingExpiresAt = now.AddDays(7)
        };
        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });
        result = await users.AddToRoleAsync(user, "User");
        if (!result.Succeeded) throw new InvalidOperationException("Could not assign user role.");

        var folder = storage.UserRoot(user.Id);
        Directory.CreateDirectory(folder);
        try { await transaction.CommitAsync(); }
        catch
        {
            // No token has been issued yet and this newly allocated folder is empty.
            Directory.Delete(folder);
            throw;
        }
        return StatusCode(StatusCodes.Status201Created, tokens.Create(user, new[] { "User" }));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request)
    {
        var user = await users.FindByNameAsync(request.Username.Trim());
        if (user is null || !user.CanAccess(clock.GetUtcNow().UtcDateTime) || await users.IsLockedOutAsync(user))
            return InvalidCredentials();

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return InvalidCredentials();
        }

        await users.ResetAccessFailedCountAsync(user);
        return Ok(tokens.Create(user, await users.GetRolesAsync(user)));
    }

    [HttpGet("me")]
    public async Task<ActionResult<CurrentUser>> Me()
    {
        var user = await users.FindByIdAsync(User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);
        if (user is null) return Unauthorized();
        return Ok(CurrentUser.From(user, await users.GetRolesAsync(user)));
    }

    [HttpPost("password-reset-request")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> RequestPasswordReset(PasswordResetRequest request, CancellationToken cancellationToken)
    {
        var user = await users.FindByNameAsync(request.Username.Trim());
        if (user is not null)
        {
            using var lease = await operations.AcquireAsync(user.Id, cancellationToken);
            var now = clock.GetUtcNow().UtcDateTime;
            // Coalesce repeated requests and never extend an account's approval deadline.
            await db.Users.Where(u => u.Id == user.Id && u.PasswordResetRequestedAt == null &&
                (u.Status == AccountStatus.Approved || (u.Status == AccountStatus.Pending && u.PendingExpiresAt > now)))
                .ExecuteUpdateAsync(update => update.SetProperty(u => u.PasswordResetRequestedAt, now), cancellationToken);
        }
        // Do not disclose whether an account exists or is still active.
        return Ok(new { message = "If this account is active, your administrator will receive the request. Contact them to receive your new password." });
    }

    [HttpPost("change-password")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var id = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        using var lease = await operations.AcquireAsync(id, cancellationToken);
        var user = await users.FindByIdAsync(id);
        if (user is null || !user.CanAccess(clock.GetUtcNow().UtcDateTime) ||
            User.FindFirst("security_stamp")?.Value != user.SecurityStamp) return Unauthorized();
        if (request.CurrentPassword == request.NewPassword)
            return BadRequest(new { message = "Choose a different new password." });
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });
        user.PasswordResetRequestedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private UnauthorizedObjectResult InvalidCredentials() =>
        Unauthorized(new { message = "Invalid credentials, or the account is locked or expired." });
}

public sealed class SignupRequest
{
    [Required, StringLength(64, MinimumLength = 3)]
    [RegularExpression(@"^[a-zA-Z0-9_.-]+$", ErrorMessage = "Use letters, numbers, dots, hyphens, or underscores for your username.")]
    public string Username { get; init; } = "";

    [Required, StringLength(1024, MinimumLength = 8)]
    public string Password { get; init; } = "";
}

public sealed class LoginRequest
{
    [Required, StringLength(256)]
    public string Username { get; init; } = "";

    [Required, StringLength(1024)]
    public string Password { get; init; } = "";
}

public sealed class PasswordResetRequest
{
    [Required, StringLength(256)]
    public string Username { get; init; } = "";
}

public sealed class ChangePasswordRequest
{
    [Required, StringLength(1024)]
    public string CurrentPassword { get; init; } = "";

    [Required, StringLength(1024, MinimumLength = 8)]
    public string NewPassword { get; init; } = "";
}
