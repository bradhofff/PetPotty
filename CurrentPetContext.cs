namespace PetPotty.Models;

public sealed record CurrentPetContext(IReadOnlyList<Pet> Pets, int? SelectedPetID, string Description);
