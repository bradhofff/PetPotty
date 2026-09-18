using System.ComponentModel.DataAnnotations;

namespace PetPotty.Models;

public static class HouseholdRoles
{
    public const string Owner = "Owner";
    public const string Member = "Member";
    public const string Caregiver = "Caregiver";

    public static readonly string[] All = [Owner, Member, Caregiver];

    // Roles an Owner is allowed to invite/assign to someone else. Owner is
    // deliberately excluded — this MVP does not support multiple Owners or
    // ownership transfer.
    public static readonly string[] Assignable = [Member, Caregiver];

    public static bool IsValid(string? role) => role != null && All.Contains(role);
}

// The three permission tiers used across every household-scoped operation.
// Centralized here so PageModels/services never compare role strings directly.
public static class HouseholdPermissions
{
    // Rename household, invite/resend/revoke, change member roles, remove members.
    public static bool CanManageHousehold(string role) => role == HouseholdRoles.Owner;

    // Add/edit a pet, manage medication definitions, manage vet visit records.
    public static bool CanManagePets(string role) => role is HouseholdRoles.Owner or HouseholdRoles.Member;

    // Delete a pet outright (cascades all of its care history) — stricter than
    // ManagePets because of the blast radius.
    public static bool CanDeletePet(string role) => role == HouseholdRoles.Owner;

    // Log tasks, confirm/unconfirm medication doses, log symptoms/incidents.
    public static bool CanRecordCare(string role) => HouseholdRoles.IsValid(role);
}

public sealed class HouseholdContext
{
    public int HouseholdID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;

    public bool CanManageHousehold => HouseholdPermissions.CanManageHousehold(Role);
    public bool CanManagePets => HouseholdPermissions.CanManagePets(Role);
    public bool CanDeletePet => HouseholdPermissions.CanDeletePet(Role);
    public bool CanRecordCare => HouseholdPermissions.CanRecordCare(Role);
}

public sealed class HouseholdSummary
{
    public int HouseholdID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public sealed class HouseholdMember
{
    public int UserID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime JoinedAtUtc { get; set; }
}

public sealed class HouseholdInvitation
{
    public int HouseholdInvitationID { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? LastSentAtUtc { get; set; }
    public string InvitedByName { get; set; } = string.Empty;
    public bool IsExpired { get; set; }
}

public sealed class InvitationInput
{
    [Required(ErrorMessage = "Enter an email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Choose a role.")]
    public string Role { get; set; } = HouseholdRoles.Member;
}

// Result of loading an invitation by its raw token, before the recipient has
// necessarily logged in — drives the Accept Invitation landing page.
public sealed class InvitationDetails
{
    public int HouseholdInvitationID { get; set; }
    public int HouseholdID { get; set; }
    public string HouseholdName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string InvitedByName { get; set; } = string.Empty;

    public InvitationStatus Status => AcceptedAtUtc.HasValue
        ? InvitationStatus.Accepted
        : RevokedAtUtc.HasValue
            ? InvitationStatus.Revoked
            : ExpiresAtUtc <= DateTime.UtcNow
                ? InvitationStatus.Expired
                : InvitationStatus.Pending;
}

public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
    Expired
}

public enum AcceptInvitationResult
{
    Accepted,
    AlreadyAccepted,
    NotFound,
    Expired,
    Revoked,
    EmailMismatch
}

public enum CreateInvitationResult
{
    Created,
    AlreadyPending,
    AlreadyMember,
    InvalidRole,
    NotAuthorized
}
