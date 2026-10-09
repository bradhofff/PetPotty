namespace PetPotty.Models;
public sealed record HealthUpcomingItem(string Kind, int RecordID, int PetID, string Title, string Detail, DateTime At, string Timing);
