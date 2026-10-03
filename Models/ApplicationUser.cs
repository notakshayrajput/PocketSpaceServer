using Microsoft.AspNetCore.Identity;

namespace PocketSpaceServer.Models;

public class ApplicationUser : IdentityUser
{
    public long QuotaBytes { get; set; } = UserQuota.DefaultBytes;
    public string Status { get; set; } = AccountStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime? PendingExpiresAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PasswordResetRequestedAt { get; set; }
    public bool IsBlocked { get; set; }

    public bool CanAccess(DateTime now) => !IsBlocked && (Status == AccountStatus.Approved ||
        (Status == AccountStatus.Pending && PendingExpiresAt > now));
}

public static class UserQuota
{
    public const long Megabyte = 1024L * 1024;
    public const long DefaultBytes = 500 * Megabyte;
}

public static class AccountStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Deleting = "Deleting";
}
