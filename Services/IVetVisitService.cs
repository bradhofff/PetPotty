using PetPotty.Models;

namespace PetPotty.Services
{
    public interface IVetVisitService
    {
        List<VetVisit> GetVisits(int userID, int householdID, int? petID = null);
        VetVisit? GetVisit(int userID, int householdID, int vetVisitID);
        int AddVisit(int userID, int householdID, VetVisitInput input, DateTime? reminderAt);
        bool UpdateVisit(int userID, int householdID, VetVisitInput input, DateTime? reminderAt);
        bool ChangeStatus(int userID, int householdID, int vetVisitID, string status, string details);
        bool CompleteVisit(int userID, int householdID, CompleteVetVisitInput input);
        bool DeleteVisit(int userID, int householdID, int vetVisitID);
        bool DismissReminder(int userID, int householdID, int reminderID);
        List<VetVisitDocument> GetDocuments(int userID, int householdID, int vetVisitID);
        VetVisitDocument? GetDocument(int userID, int householdID, int documentID);
        int AddDocument(int userID, int householdID, VetVisitDocument document);
        bool UpdateDocument(int userID, int householdID, int documentID, string documentType, string displayName, string description);
        VetVisitDocument? DeleteDocument(int userID, int householdID, int documentID);
        List<VetVisitHistory> GetHistory(int userID, int householdID, int vetVisitID);
        List<DashboardCareItem> GetDashboardVisits(int userID, int householdID, DateTime startDate, DateTime endDate);
        List<string> GetDocumentPathsByPet(int userID, int householdID, int petID);
    }
}
