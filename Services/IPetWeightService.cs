using PetPotty.Models;

namespace PetPotty.Services;

public interface IPetWeightService
{
    List<PetWeightEntry> GetWeights(int userID, int petID);
    bool SaveWeight(int userID, PetWeightInput input);
    bool DeleteWeight(int userID, int petID, int weightEntryID);
}
