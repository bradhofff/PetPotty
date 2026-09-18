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
        List<Pet> GetPetsByUser(int userID);
        List<TaskItem> GetTasksByPetID(int userID, int petID, bool allTime);
        List<TaskItem> GetTasksByPetIDSince(int userID, int petID, DateTime startDate, out bool hasOlderTasks);
        List<TaskItem> GetLatestActivityTasksByPetID(int userID, int petID);
        Pet? GetPetByID(int userID, int petID);
        int AddPet(int userID, string name, string type, string breed, string age, DateTime birthdate, string gender);
        bool EditPet(int userID, int petID, string name, string type, string breed, string age, DateTime birthdate, string gender);
        bool UpdatePetProfileImagePath(int userID, int petID, string? profileImagePath);
        bool DeletePet(int userID, int petID);
        bool AddTask(int userID, int petID, string taskType, string notes, DateTime createdAt);
        bool UpdateTask(int userID, int taskID, string taskType, string notes, DateTime createdAt);
        bool DeleteTask(int userID, int taskID);
    }
}
