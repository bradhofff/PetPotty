namespace PetPotty.Models;
public sealed record PetCardReminders(int PetID, List<DashboardCareItem> Items, bool Failed, int Offset);
