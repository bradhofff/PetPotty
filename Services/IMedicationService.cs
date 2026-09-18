using PetPotty.Models;

namespace PetPotty.Services
{
    public interface IMedicationService
    {
        List<Medication> GetMedicationsByPetID(int userID, int householdID, int petID);
        List<MedSchedule> GetScheduleByPetID(int userID, int householdID, int petID, bool showAllTime);
        int AddMedication(int userID, int householdID, int petID, string medicationName, string dosage,
                           string frequencyType, int? frequencyInterval, bool timingDoesNotMatter,
                           DateTime startDate, DateTime? endDate, string notes);
        bool UpdateMedication(int userID, int householdID, int medID, string medicationName, string dosage,
                              string frequencyType, int? frequencyInterval, bool timingDoesNotMatter,
                              DateTime startDate, DateTime? endDate, string notes);
        bool DeleteMedication(int userID, int householdID, int medID);
        void ConfirmSchedule(int userID, int householdID, int medID, DateTime logDate, DateTime confirmedAt,
                              int recordedByUserID, DateTime administeredAtUtc, string doseStatus,
                              string? administrationNotes = null);
        void UnconfirmSchedule(int userID, int householdID, int medID, DateTime logDate);
    }
}
