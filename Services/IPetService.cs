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
        List<TaskItem> GetTasksByPetID(int petID, bool allTime);
        void AddPet(int userID, string name, string type, string breed, string age, DateTime birthdate, string gender);
        void EditPet(int petID, string name, string type, string breed, string age, DateTime birthdate, string gender);
        void DeletePet(int petID);
        void AddTask(int petID, string taskType, string notes, DateTime createdAt);
        void UpdateTask(int taskID, string taskType, string notes, DateTime createdAt);
        void DeleteTask(int taskID);
    }
}
