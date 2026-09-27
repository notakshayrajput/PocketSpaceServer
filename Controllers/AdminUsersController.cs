using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = "Admin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AdminUsersController(ApplicationDbContext db, AccountOperationLocks operations, TimeProvider clock,
    UserManager<ApplicationUser> users) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> ListUsers(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return Ok(await db.Users.AsNoTracking()
            .Where(u => u.Status == AccountStatus.Approved ||
                (u.Status == AccountStatus.Pending && u.PendingExpiresAt > now))
            .OrderBy(u => u.UserName)
            .Select(u => new { u.Id, Username = u.UserName, u.Status, u.QuotaBytes })
            .ToListAsync(cancellationToken));
    }

    [HttpPut("{id}/quota")]
    public async Task<IActionResult> IncreaseQuota(string id, ChangeQuotaRequest request,
        CancellationToken cancellationToken)
    {
        if (request.QuotaBytes < UserQuota.DefaultBytes ||
            request.QuotaBytes % UserQuota.Megabyte != 0)
            return BadRequest(new { message = "Enter a quota of at least 500 MB in whole MB." });
        using var lease = await operations.AcquireAsync(id, cancellationToken);
        var account = await db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (account is null) return NotFound();
        if (!account.CanAccess(clock.GetUtcNow().UtcDateTime))
            return Conflict(new { message = "This account is no longer active." });
        if (request.QuotaBytes <= account.QuotaBytes)
            return Conflict(new { message = "The new quota must be larger than the current quota." });
        account.QuotaBytes = request.QuotaBytes;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
    [HttpGet("password-reset-requests")]
    public async Task<IActionResult> PasswordResetRequests(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return Ok(await db.Users.AsNoTracking().Where(u => u.PasswordResetRequestedAt != null &&
            (u.Status == AccountStatus.Approved || (u.Status == AccountStatus.Pending && u.PendingExpiresAt > now)))
            .OrderBy(u => u.PasswordResetRequestedAt)
            .Select(u => new { u.Id, Username = u.UserName, u.PasswordResetRequestedAt })
            .ToListAsync(cancellationToken));
    }

    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(string id, AdminResetPasswordRequest request, CancellationToken cancellationToken)
    {
        using var lease = await operations.AcquireAsync(id, cancellationToken);
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (!user.CanAccess(clock.GetUtcNow().UtcDateTime) || user.PasswordResetRequestedAt is null)
            return Conflict(new { message = "This account no longer has an active password reset request." });
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });
        user.PasswordResetRequestedAt = null;
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("pending")]
    public async Task<IActionResult> Pending(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return Ok(await db.Users.AsNoTracking().Where(u => u.Status == AccountStatus.Pending && u.PendingExpiresAt > now)
            .OrderBy(u => u.CreatedAt)
            .Select(u => new { u.Id, Username = u.UserName, u.CreatedAt, u.PendingExpiresAt })
            .ToListAsync(cancellationToken));
    }

    [HttpPost("{id}/approve")]
    public async Task<IActionResult> Approve(string id, CancellationToken cancellationToken)
    {
        using var lease = await operations.AcquireAsync(id, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var changed = await db.Users.Where(u => u.Id == id && u.Status == AccountStatus.Pending && u.PendingExpiresAt > now)
            .ExecuteUpdateAsync(update => update.SetProperty(u => u.Status, AccountStatus.Approved)
                .SetProperty(u => u.ApprovedAt, now).SetProperty(u => u.PendingExpiresAt, (DateTime?)null), cancellationToken);
        if (changed == 1) return NoContent();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null) return NotFound();
        return user.Status == AccountStatus.Approved ? NoContent() :
            Conflict(new { message = "This account has expired and can no longer be approved." });
    }
}

public sealed class AdminResetPasswordRequest
{
    [Required, StringLength(1024, MinimumLength = 8)]
    public string NewPassword { get; init; } = "";
}

public sealed record ChangeQuotaRequest(long QuotaBytes);
