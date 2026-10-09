using PetPotty.Models;

namespace PetPotty.Services;

// Navigation state only. Callers must obtain accessiblePets from the active household.
public static class SelectedPetPreference
{
    public const string SessionKey = "care-selected-pet";
    private const string HouseholdKey = "care-selected-pet-household";
    private const string OwnerKey = "care-selected-pet-user";
    private static readonly string[] LegacyKeys =
        ["health-selected-pet", "medicationsSelectedPetID", "vet-visits-selected-pet"];

    public static int? Get(ISession session, IReadOnlyCollection<Pet> accessiblePets)
    {
        RemoveLegacy(session);
        var id = session.GetInt32(SessionKey);
        if (session.GetString(OwnerKey) != session.GetString("userID")
            || session.GetInt32(HouseholdKey) != session.GetInt32("activeHouseholdID")
            || (id.HasValue && !accessiblePets.Any(pet => pet.PetID == id)))
        {
            Clear(session);
            return null;
        }
        return id;
    }

    // Null/zero is an explicit clear; absence of a choice should call Get instead.
    public static bool TrySelect(ISession session, IReadOnlyCollection<Pet> accessiblePets, int? id)
    {
        Get(session, accessiblePets);
        if (id is null or 0)
        {
            Clear(session);
            return true;
        }
        if (!accessiblePets.Any(pet => pet.PetID == id))
            return false;
        if (session.GetInt32("activeHouseholdID") is int householdID)
            session.SetInt32(HouseholdKey, householdID);
        session.SetInt32(SessionKey, id.Value);
        session.SetString(OwnerKey, session.GetString("userID") ?? "");
        return true;
    }

    public static void Clear(ISession session)
    {
        session.Remove(SessionKey);
        session.Remove(OwnerKey);
        session.Remove(HouseholdKey);
        // Pending record navigation must not reselect a pet after a boundary or clear.
        foreach (var key in new[] { "health-edit-event", "medications-focus-medication", "vet-visits-focus-visit", "vet-visits-open-edit", "vet-visits-open-add" })
            session.Remove(key);
        RemoveLegacy(session);
    }

    private static void RemoveLegacy(ISession session)
    {
        foreach (var key in LegacyKeys) session.Remove(key);
    }
}
