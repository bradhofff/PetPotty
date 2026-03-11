namespace PetPotty.Models
{
    public class TaskItem
    {
        public int TaskID { get; set; }
        public int PetID { get; set; }  // ← this line must exist
        public string PetName { get; set; } = string.Empty;
        public string TaskType { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}