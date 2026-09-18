using PetPotty.Models;

namespace PetPotty.Services;

public interface IHouseholdAuthorizationService
{
    HouseholdRole? GetRole(int userID, int householdID);
    bool HasPermission(int userID, int householdID, HouseholdPermission permission);
}

