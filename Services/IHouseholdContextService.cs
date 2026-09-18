using Microsoft.AspNetCore.Http;
using PetPotty.Models;

namespace PetPotty.Services;

// The single place that resolves "which household is this request acting
// on." Every household-scoped page/handler should call GetActiveHousehold
// once and pass its HouseholdID into services — never trust a HouseholdID
// that arrived from the client directly.
public interface IHouseholdContextService
{
    // Re-validates membership against the database on every call (a removed
    // member falls back safely instead of keeping stale session access).
    // Returns null only if the user has no active household at all.
    HouseholdContext? GetActiveHousehold(ISession session, int userID);

    List<HouseholdSummary> GetHouseholds(int userID);

    // Switches the session's active household after confirming the user is
    // still an active member of the requested one. Returns false (and
    // leaves the session untouched) if they are not.
    bool SwitchHousehold(ISession session, int userID, int requestedHouseholdID);
}
