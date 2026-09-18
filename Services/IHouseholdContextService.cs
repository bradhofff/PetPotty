using PetPotty.Models;

namespace PetPotty.Services;

public interface IHouseholdContextService
{
    HouseholdContext? GetActiveHousehold(int userID);
    bool TrySetActiveHousehold(int userID, Guid publicID);
    bool TrySetActiveHousehold(int userID, int householdID);
    void ClearActiveHousehold();
}

