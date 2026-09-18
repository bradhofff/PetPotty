using PetPotty.Models;

namespace PetPotty.Services;

public interface IHouseholdService
{
    HouseholdContext EnsurePersonalHousehold(int userID);
    IReadOnlyList<HouseholdContext> GetHouseholds(int userID);
    IReadOnlyList<HouseholdMember> GetMembers(int userID, int householdID);
    IReadOnlyList<HouseholdInvitation> GetPendingInvitations(int userID, int householdID);
    bool RenameHousehold(int userID, int householdID, string name);
    bool ChangeMemberRole(int userID, int householdID, Guid memberPublicID, HouseholdRole role);
    bool RemoveMember(int userID, int householdID, Guid memberPublicID);
}

