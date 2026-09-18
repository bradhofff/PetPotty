namespace PetPotty.Models;

public enum HouseholdRole
{
    Owner,
    Member,
    Caregiver
}

public enum HouseholdPermission
{
    ViewPets,
    RecordCare,
    ManageCarePlans,
    ManagePets,
    ManageHousehold,
    ManageMembers
}

public static class HouseholdAccessRules
{
    public static bool HasPermission(HouseholdRole role, HouseholdPermission permission) => permission switch
    {
        HouseholdPermission.ViewPets => true,
        HouseholdPermission.RecordCare => true,
        HouseholdPermission.ManageCarePlans => role is HouseholdRole.Owner or HouseholdRole.Member,
        HouseholdPermission.ManagePets => role == HouseholdRole.Owner,
        HouseholdPermission.ManageHousehold => role == HouseholdRole.Owner,
        HouseholdPermission.ManageMembers => role == HouseholdRole.Owner,
        _ => false
    };

    public static bool TryParseRole(string? value, out HouseholdRole role) =>
        Enum.TryParse(value, ignoreCase: true, out role);

    public static string ToDatabaseValue(HouseholdRole role) => role.ToString();
}

public sealed class HouseholdContext
{
    public int HouseholdID { get; init; }
    public Guid PublicID { get; init; }
    public string Name { get; init; } = string.Empty;
    public HouseholdRole Role { get; init; }
}

public sealed class HouseholdMember
{
    public Guid UserPublicID { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public HouseholdRole Role { get; init; }
    public DateTime JoinedAtUtc { get; init; }
    public bool IsCurrentUser { get; init; }
}

public sealed class HouseholdInvitation
{
    public Guid PublicID { get; init; }
    public string Email { get; init; } = string.Empty;
    public HouseholdRole Role { get; init; }
    public string InvitedByName { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
}

public sealed class HouseholdInvitationDetails
{
    public Guid PublicID { get; init; }
    public int HouseholdID { get; init; }
    public string HouseholdName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public HouseholdRole Role { get; init; }
    public string InvitedByName { get; init; } = string.Empty;
    public DateTime ExpiresAtUtc { get; init; }
    public DateTime? AcceptedAtUtc { get; init; }
    public DateTime? RevokedAtUtc { get; init; }
}

public sealed record HouseholdInvitationResult(bool Succeeded, string Message, bool EmailSent = false);
