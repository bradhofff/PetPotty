using PetPotty.Models;

namespace PetPotty.Services;

public interface IHouseholdService
{
    // Idempotent: returns the user's existing household if they already
    // have one, otherwise creates one and makes them its Owner.
    int EnsureDefaultHousehold(int userID, string userName);

    List<HouseholdMember> GetMembers(int userID, int householdID);
    bool RenameHousehold(int userID, int householdID, string newName);
    bool UpdateMemberRole(int userID, int householdID, int targetUserID, string newRole);
    bool RemoveMember(int userID, int householdID, int targetUserID);
}
