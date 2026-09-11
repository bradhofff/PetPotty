using PetPotty.Models;

namespace PetPotty.Services
{
    public interface IMedicationService
    {
        bool OwnsMedication(int userID, int medID);
        List<Medication> GetMedicationsByPetID(int petID);
        List<MedSchedule> GetScheduleByPetID(int petID, bool showAllTime, int utcOffsetMinutes = 0);
        bool AddMedication(int userID, int petID, string medicationName, string dosage,
                           string frequencyType, int? frequencyInterval, bool timingDoesNotMatter,
                           DateTime startDate, DateTime? endDate, string notes);
        bool UpdateMedication(int userID, int medID, string medicationName, string dosage,
                              string frequencyType, int? frequencyInterval, bool timingDoesNotMatter,
                              DateTime startDate, DateTime? endDate, string notes);
        bool DeleteMedication(int userID, int medID);
        bool ConfirmSchedule(int userID, int medID, DateTime logDate, DateTime confirmedAt,
                             int utcOffsetMinutes, string notes);
        bool RecordDose(int userID, int medID, DateTime logDate, string status,
                        DateTime? administeredAtLocal, int utcOffsetMinutes,
                        string reason, string notes);
        bool UnconfirmSchedule(int userID, int medID, DateTime logDate);
    }
}
