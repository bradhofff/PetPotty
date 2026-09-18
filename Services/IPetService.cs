// ============================================================
// Services/IPetService.cs — The interface
//
// KEY .NET CORE CONCEPT: Program against interfaces, not concrete classes.
// The DI container maps IPetService → PetService at startup (in Program.cs).
// PageModels declare IPetService in their constructor — they never know or
// care that PetService is the actual implementation. This means:
//   - Easy to unit test (swap in a mock implementation)
//   - Easy to swap data sources later (e.g., move to Entity Framework)
// ============================================================

using PetPotty.Models;

namespace PetPotty.Services
{
    public interface IPetService
    {
        List<Pet> GetPetsByHousehold(int userID, int householdID);
        Pet? GetPetByID(int userID, int householdID, int petID);
        List<TaskItem> GetTasksByPetID(int userID, int householdID, int petID, bool allTime);
        List<TaskItem> GetTasksByPetIDSince(int userID, int householdID, int petID, DateTime startDate, out bool hasOlderTasks);
        List<TaskItem> GetLatestActivityTasksByPetID(int userID, int householdID, int petID);
        int AddPet(int userID, int householdID, string name, string type, string breed, string age, DateTime birthdate, string gender);
        bool EditPet(int userID, int householdID, int petID, string name, string type, string breed, string age, DateTime birthdate, string gender);
        void UpdatePetProfileImagePath(int petID, string? profileImagePath);
        bool DeletePet(int userID, int householdID, int petID);
        bool AddTask(int userID, int householdID, int petID, string taskType, string notes, DateTime createdAt);
        bool UpdateTask(int userID, int householdID, int taskID, string taskType, string notes, DateTime createdAt);
        bool DeleteTask(int userID, int householdID, int taskID);
    }
}
