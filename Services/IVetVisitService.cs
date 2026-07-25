using PetPotty.Models;

namespace PetPotty.Services
{
    public interface IVetVisitService
    {
        List<VetVisit> GetVisits(int userID, int? petID = null);
        VetVisit? GetVisit(int userID, int vetVisitID);
        int AddVisit(int userID, VetVisitInput input, DateTime? reminderAt);
        bool UpdateVisit(int userID, VetVisitInput input, DateTime? reminderAt);
        bool ChangeStatus(int userID, int vetVisitID, string status, string details);
        bool CompleteVisit(int userID, CompleteVetVisitInput input);
        bool DeleteVisit(int userID, int vetVisitID);
        bool DismissReminder(int userID, int reminderID);
        List<VetVisitDocument> GetDocuments(int userID, int vetVisitID);
        VetVisitDocument? GetDocument(int userID, int documentID);
        int AddDocument(int userID, VetVisitDocument document);
        bool UpdateDocument(int userID, int documentID, string documentType, string displayName, string description);
        VetVisitDocument? DeleteDocument(int userID, int documentID);
        List<VetVisitHistory> GetHistory(int userID, int vetVisitID);
        List<DashboardCareItem> GetDashboardVisits(int userID, DateTime startDate, DateTime endDate);
        List<string> GetDocumentPathsByPet(int userID, int petID);
    }
}
