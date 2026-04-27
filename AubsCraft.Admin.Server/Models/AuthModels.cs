namespace AubsCraft.Admin.Server.Models;

public static class Roles
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string Friend = "Friend";

    public const string OwnerOrAdmin = "Owner,Admin";
    public const string AnyUser = "Owner,Admin,Friend";
}

public class User
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Role { get; set; } = Roles.Friend;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedViaInviteCode { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public class UsersFile
{
    public List<User> Users { get; set; } = new();
}

public class InviteCode
{
    public string Code { get; set; } = "";
    public int MaxUses { get; set; }
    public int UsesRemaining { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string Notes { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Revoked { get; set; }
    public List<InviteRedemption> Redemptions { get; set; } = new();

    public bool IsValid(DateTime now)
        => !Revoked && UsesRemaining > 0 && (ExpiresAt == null || ExpiresAt > now);
}

public class InviteRedemption
{
    public string Username { get; set; } = "";
    public DateTime RedeemedAt { get; set; } = DateTime.UtcNow;
}

public class InviteCodesFile
{
    public List<InviteCode> Codes { get; set; } = new();
}

public class WhitelistAuditEntry
{
    public string McUsername { get; set; } = "";
    public string Platform { get; set; } = "Java";
    public string AddedByWebUser { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public bool AutoAdded { get; set; }
    public bool Confirmed { get; set; }
}

public class WhitelistAuditFile
{
    public List<WhitelistAuditEntry> Entries { get; set; } = new();
}

public record AuthStatusDto(
    bool Authenticated,
    bool NeedsSetup,
    string? Username,
    string? Role);

public record LoginRequest(string Username, string Password);

public record RedeemRequest(string Code, string Username, string Password);

public record CreateInviteCodeRequest(
    string? Code,
    int MaxUses,
    int? ExpiresInDays,
    string Notes);

public record InviteCodeDto(
    string Code,
    int MaxUses,
    int UsesRemaining,
    DateTime? ExpiresAt,
    string Notes,
    string CreatedBy,
    DateTime CreatedAt,
    bool Revoked,
    bool IsValid,
    List<InviteRedemptionDto> Redemptions);

public record InviteRedemptionDto(string Username, DateTime RedeemedAt);

public record AddOwnMcAccountRequest(string McUsername, string Platform);

public record WhitelistAuditEntryDto(
    string McUsername,
    string Platform,
    string AddedByWebUser,
    DateTime AddedAt,
    bool AutoAdded,
    bool Confirmed);

public record PublicStatusDto(
    bool Connected,
    int Online,
    int Max,
    List<string> Players);

public record UserSummaryDto(
    string Username,
    string Role,
    DateTime CreatedAt,
    string? CreatedViaInviteCode,
    DateTime? LastLoginAt);
