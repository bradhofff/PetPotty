using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages
{
    public class HomeModel : PageModel
    {
        private readonly IPetService _petService;

        public HomeModel(IPetService petService)
        {
            _petService = petService;
        }

        public string UserName { get; set; } = string.Empty;
        public int UserID { get; set; }
        public List<Pet> Pets { get; set; } = new();
        public Dictionary<int, List<TaskItem>> PetTasks { get; set; } = new();
        public Dictionary<int, List<TaskItem>> PetAllTasks { get; set; } = new();

        public bool ShowAllTime { get; set; } = false;

        // Add Pet fields
        [BindProperty] public string NewPetName { get; set; } = string.Empty;
        [BindProperty] public string NewPetType { get; set; } = string.Empty;
        [BindProperty] public string NewPetBreed { get; set; } = string.Empty;
        [BindProperty] public string NewPetAge { get; set; } = string.Empty;
        [BindProperty] public DateTime NewPetBirthdate { get; set; } = DateTime.Today;
        [BindProperty] public string NewPetGender { get; set; } = string.Empty;

        // Edit Pet fields
        [BindProperty] public int EditPetID { get; set; }
        [BindProperty] public string EditPetName { get; set; } = string.Empty;
        [BindProperty] public string EditPetType { get; set; } = string.Empty;
        [BindProperty] public string EditPetBreed { get; set; } = string.Empty;
        [BindProperty] public string EditPetAge { get; set; } = string.Empty;
        [BindProperty] public DateTime EditPetBirthdate { get; set; } = DateTime.Today;
        [BindProperty] public string EditPetGender { get; set; } = string.Empty;

        // Add Task fields
        [BindProperty] public int NewTaskPetID { get; set; }
        [BindProperty] public string NewTaskType { get; set; } = string.Empty;
        [BindProperty] public string NewTaskNotes { get; set; } = string.Empty;
        [BindProperty] public DateTime NewTaskCreatedAt { get; set; } = DateTime.Now;

        // Update Task fields
        [BindProperty] public int UpdateTaskID { get; set; }
        [BindProperty] public string UpdateTaskType { get; set; } = string.Empty;
        [BindProperty] public string UpdateTaskNotes { get; set; } = string.Empty;
        [BindProperty] public DateTime UpdateTaskCreatedAt { get; set; } = DateTime.Now;

        // ============================================================
        // GET
        // ============================================================
        public IActionResult OnGet()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            UserName = HttpContext.Session.GetString("name") ?? string.Empty;
            ShowAllTime = GetShowAllTime();

            LoadData();
            return Page();
        }

        // ============================================================
        // Add Pet
        // ============================================================
        public IActionResult OnPostAddPet()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            _petService.AddPet(UserID, NewPetName, NewPetType, NewPetBreed,
                               NewPetAge, NewPetBirthdate, NewPetGender);

            TempData["StatusMessage"] = $"{NewPetName} has been added!";
            return RedirectToPage();
        }

        // ============================================================
        // Edit Pet
        // ============================================================
        public IActionResult OnPostEditPet()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            _petService.EditPet(EditPetID, EditPetName, EditPetType, EditPetBreed,
                                EditPetAge, EditPetBirthdate, EditPetGender);

            TempData["StatusMessage"] = $"{EditPetName} has been updated!";
            return RedirectToPage();
        }

        // ============================================================
        // Delete Pet
        // ============================================================
        public IActionResult OnPostDeletePet()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            _petService.DeletePet(EditPetID);

            TempData["StatusMessage"] = "Pet has been deleted.";
            return RedirectToPage();
        }

        // ============================================================
        // Quick Log — instant Pee or Poop with current time, no modal
        // ============================================================
        public IActionResult OnPostQuickLog(int petID, string taskType, string? localTime = null)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            ShowAllTime = GetShowAllTime();
            var timestamp = (!string.IsNullOrEmpty(localTime) && DateTime.TryParse(localTime, out var parsed))
                ? parsed
                : DateTime.Now;
            _petService.AddTask(petID, taskType, string.Empty, timestamp);

            var emoji = taskType == "Pee" ? "💧" : "💩";
            var petName = GetPetName(UserID, petID);
            TempData["StatusMessage"] = string.IsNullOrWhiteSpace(petName)
                ? $"{emoji} {taskType} logged successfully!"
                : $"{emoji} {taskType} logged successfully for {petName}!";
            return RedirectToPage();
        }

        public IActionResult OnPostSetTaskView(bool showAllTime)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            HttpContext.Session.SetString("homeShowAllTime", showAllTime.ToString());
            return RedirectToPage();
        }

        // ============================================================
        // Add Task (modal)
        // ============================================================
        public IActionResult OnPostAddTask()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            ShowAllTime = GetShowAllTime();
            _petService.AddTask(NewTaskPetID, NewTaskType, NewTaskNotes, NewTaskCreatedAt);

            var petName = GetPetName(UserID, NewTaskPetID);
            TempData["StatusMessage"] = string.IsNullOrWhiteSpace(petName)
                ? "Task added successfully!"
                : $"Task added successfully for {petName}!";
            return RedirectToPage();
        }

        // ============================================================
        // Update Task
        // ============================================================
        public IActionResult OnPostUpdateTask()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            ShowAllTime = GetShowAllTime();
            _petService.UpdateTask(UpdateTaskID, UpdateTaskType, UpdateTaskNotes, UpdateTaskCreatedAt);

            TempData["StatusMessage"] = "Task updated successfully!";
            return RedirectToPage();
        }

        // ============================================================
        // Delete Task
        // ============================================================
        public IActionResult OnPostDeleteTask(int taskID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            ShowAllTime = GetShowAllTime();
            _petService.DeleteTask(taskID);

            TempData["StatusMessage"] = "Task deleted.";
            return RedirectToPage();
        }

        // ============================================================
        // Public helpers
        // ============================================================
        public static string TaskTypeEmoji(string taskType) => taskType switch
        {
            "Pee"  => "💧 Pee",
            "Poop" => "💩 Poop",
            "Walk" => "🦮 Walk",
            "Play" => "🎾 Play",
            _      => taskType
        };

        public static string LastActivityLabel(List<TaskItem> tasks, string taskType)
        {
            if (tasks.Count == 0)
                return "No tasks found";

            var lastTask = tasks
                .Where(task => IsActivityMatch(task.TaskType, taskType))
                .OrderByDescending(task => task.CreatedAt)
                .FirstOrDefault();

            if (lastTask == null)
                return $"No {taskType.ToLower()} found";

            var elapsed = DateTime.Now - lastTask.CreatedAt;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            if (elapsed.TotalDays > 7)
                return $"Last {taskType.ToLower()}: More than a week ago...";

            var timeText = lastTask.CreatedAt.ToString("h:mm tt");
            if (elapsed.TotalHours > 24)
                timeText = $"{lastTask.CreatedAt:dddd} {timeText}";

            return $"Last {taskType.ToLower()}: {timeText}";
        }

        // ============================================================
        // Private helpers
        // ============================================================
        private static bool IsActivityMatch(string taskType, string activityType)
        {
            return taskType == activityType || taskType == "Pee & Poop" && (activityType == "Pee" || activityType == "Poop");
        }

        private string? GetPetName(int userID, int petID)
        {
            return _petService.GetPetsByUser(userID)
                .FirstOrDefault(pet => pet.PetID == petID)?.Name;
        }

        private bool GetShowAllTime()
        {
            return bool.TryParse(HttpContext.Session.GetString("homeShowAllTime"), out var showAllTime) && showAllTime;
        }

        private void LoadData()
        {
            Pets = _petService.GetPetsByUser(UserID);
            foreach (var pet in Pets)
            {
                PetTasks[pet.PetID] = _petService.GetTasksByPetID(pet.PetID, ShowAllTime);
                PetAllTasks[pet.PetID] = ShowAllTime ? PetTasks[pet.PetID] : _petService.GetTasksByPetID(pet.PetID, true);
            }
        }
    }
}
