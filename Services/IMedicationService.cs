using PetPotty.Models;

namespace PetPotty.Services
{
    public interface IMedicationService
    {
        List<Medication> GetMedicationsByPetID(int petID);
        List<MedSchedule> GetScheduleByPetID(int petID, bool allTime);
        void AddMedication(int petID, string medicationName, string dosage,
                           string frequencyType, int? frequencyInterval,
                           DateTime startDate, DateTime? endDate, string notes);
        void UpdateMedication(int medID, string medicationName, string dosage,
                              string frequencyType, int? frequencyInterval,
                              DateTime startDate, DateTime? endDate, string notes);
        void DeleteMedication(int medID);
        void ConfirmSchedule(int medID, DateTime logDate);
        void UnconfirmSchedule(int medID, DateTime logDate);
    }
}
