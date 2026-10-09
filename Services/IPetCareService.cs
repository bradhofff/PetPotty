using PetPotty.Models;
namespace PetPotty.Services;
public interface IPetCareService
{
    PetCareProfile? Get(int userID, int petID);
    bool SaveSection(int userID, int petID, string section, IReadOnlyDictionary<string, string?> values);
}
