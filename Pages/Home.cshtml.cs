using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages
{
    public class HomeModel : PageModel
    {
        private readonly IPetService _petService;
        private readonly IMedicationService _medicationService;
        private readonly IVetVisitService _vetVisitService;
        private readonly IPetImageStorage _petImageStorage;
        private readonly IVetVisitDocumentStorage _vetVisitDocumentStorage;
        private readonly ILogger<HomeModel> _logger;

        public HomeModel(
            IPetService petService,
            IMedicationService medicationService,
            IVetVisitService vetVisitService,
            IPetImageStorage petImageStorage,
            IVetVisitDocumentStorage vetVisitDocumentStorage,
            ILogger<HomeModel> logger)
        {
            _petService = petService;
            _medicationService = medicationService;
            _vetVisitService = vetVisitService;
            _petImageStorage = petImageStorage;
            _vetVisitDocumentStorage = vetVisitDocumentStorage;
            _logger = logger;
        }

        public string UserName { get; set; } = string.Empty;
        public int UserID { get; set; }
        public List<Pet> Pets { get; set; } = new();
        public Dictionary<int, List<TaskItem>> PetTasks { get; set; } = new();
        public Dictionary<int, List<TaskItem>> PetAllTasks { get; set; } = new();
        public Dictionary<int, List<DashboardCareItem>> PetCareItems { get; set; } = new();

        public bool ShowAllTime { get; set; } = false;
        public string? PetImageError { get; set; }
        public string? ModalToOpen { get; set; }
        public string? EditPetCurrentImagePath { get; set; }

        // Add Pet fields
        [BindProperty] public string NewPetName { get; set; } = string.Empty;
        [BindProperty] public string NewPetType { get; set; } = string.Empty;
        [BindProperty] public string NewPetBreed { get; set; } = string.Empty;
        [BindProperty] public string NewPetAge { get; set; } = string.Empty;
        [BindProperty] public DateTime NewPetBirthdate { get; set; } = DateTime.Today;
        [BindProperty] public string NewPetGender { get; set; } = string.Empty;
        [BindProperty] public IFormFile? NewPetImage { get; set; }

        // Edit Pet fields
        [BindProperty] public int EditPetID { get; set; }
        [BindProperty] public string EditPetName { get; set; } = string.Empty;
        [BindProperty] public string EditPetType { get; set; } = string.Empty;
        [BindProperty] public string EditPetBreed { get; set; } = string.Empty;
        [BindProperty] public string EditPetAge { get; set; } = string.Empty;
        [BindProperty] public DateTime EditPetBirthdate { get; set; } = DateTime.Today;
        [BindProperty] public string EditPetGender { get; set; } = string.Empty;
        [BindProperty] public IFormFile? EditPetImage { get; set; }

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
        public async Task<IActionResult> OnPostAddPetAsync()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            var validationError = _petImageStorage.Validate(NewPetImage);
            if (validationError != null)
                return ShowPetModalError("addPetModal", validationError);

            var petID = 0;
            string? savedImagePath = null;
            try
            {
                petID = _petService.AddPet(UserID, NewPetName, NewPetType, NewPetBreed,
                                           NewPetAge, NewPetBirthdate, NewPetGender);

                if (NewPetImage != null && NewPetImage.Length > 0)
                {
                    savedImagePath = await _petImageStorage.SaveAsync(petID, NewPetImage, HttpContext.RequestAborted);
                    _petService.UpdatePetProfileImagePath(petID, savedImagePath);
                }
            }
            catch (Exception ex)
            {
                _petImageStorage.Delete(savedImagePath);
                if (petID != 0)
                {
                    try { _petService.DeletePet(petID); }
                    catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "Could not roll back pet {PetID} after image upload failed", petID); }
                }

                _logger.LogError(ex, "Could not add pet and profile image for user {UserID}", UserID);
                return ShowPetModalError("addPetModal", "The pet photo could not be saved. Please try again.");
            }

            TempData["StatusMessage"] = $"{NewPetName} has been added!";
            return RedirectToPage();
        }

        // ============================================================
        // Edit Pet
        // ============================================================
        public async Task<IActionResult> OnPostEditPetAsync()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            var pet = _petService.GetPetByID(UserID, EditPetID);
            if (pet == null)
                return ShowPetModalError("editPetModal", "That pet could not be found.");

            EditPetCurrentImagePath = pet.ProfileImagePath;
            var validationError = _petImageStorage.Validate(EditPetImage);
            if (validationError != null)
                return ShowPetModalError("editPetModal", validationError);

            string? newImagePath = null;
            try
            {
                if (EditPetImage != null && EditPetImage.Length > 0)
                    newImagePath = await _petImageStorage.SaveAsync(EditPetID, EditPetImage, HttpContext.RequestAborted);

                _petService.EditPet(EditPetID, EditPetName, EditPetType, EditPetBreed,
                                    EditPetAge, EditPetBirthdate, EditPetGender);

                if (newImagePath != null)
                {
                    _petImageStorage.Delete(pet.ProfileImagePath);
                    _petService.UpdatePetProfileImagePath(EditPetID, newImagePath);
                }
            }
            catch (Exception ex)
            {
                _petImageStorage.Delete(newImagePath);
                _logger.LogError(ex, "Could not update pet {PetID} and its profile image", EditPetID);
                return ShowPetModalError("editPetModal", "The pet photo could not be saved. Please try again.");
            }

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
            var pet = _petService.GetPetByID(UserID, EditPetID);
            if (pet == null)
                return RedirectToPage();

            var documentPaths = _vetVisitService.GetDocumentPathsByPet(UserID, EditPetID);
            _petService.DeletePet(EditPetID);
            _petImageStorage.Delete(pet.ProfileImagePath);
            foreach (var path in documentPaths)
                _vetVisitDocumentStorage.Delete(path);

            TempData["StatusMessage"] = "Pet has been deleted.";
            return RedirectToPage();
        }

        // ============================================================
        // Reset Pet Profile Image
        // ============================================================
        public IActionResult OnPostResetPetImage()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            var pet = _petService.GetPetByID(UserID, EditPetID);
            if (pet == null)
                return RedirectToPage();

            // Clear the database reference first so a filesystem cleanup issue
            // can never leave the UI pointing at a missing image.
            _petService.UpdatePetProfileImagePath(EditPetID, null);
            _petImageStorage.Delete(pet.ProfileImagePath);

            TempData["StatusMessage"] = $"{pet.Name}'s profile picture was reset.";
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

        public static string CareItemLabel(DashboardCareItem item)
        {
            var days = (item.DueAt.Date - DateTime.Today).Days;
            var dayLabel = days switch
            {
                < -1 => $"was due {-days} days ago",
                -1 => "was due yesterday",
                0 => "due today",
                1 => "due tomorrow",
                _ => $"due in {days} days"
            };
            var timeLabel = item.DueAt.ToString("h:mm tt");

            if (item.Kind == "Medication")
                return $"{item.Text} {dayLabel} at {timeLabel}";

            var allDay = item.Text.Contains("(all day)", StringComparison.OrdinalIgnoreCase);
            var reason = item.Text.Replace("Vet visit (all day) — ", string.Empty)
                .Replace("Vet visit — ", string.Empty);
            var visitTiming = allDay
                ? dayLabel.Replace("due ", string.Empty)
                : $"{dayLabel.Replace("due ", string.Empty)} at {timeLabel}";
            return $"Vet visit {visitTiming} · {reason}";
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

        private PageResult ShowPetModalError(string modalID, string message)
        {
            PetImageError = message;
            ModalToOpen = modalID;
            UserName = HttpContext.Session.GetString("name") ?? string.Empty;
            ShowAllTime = GetShowAllTime();
            LoadData();
            return Page();
        }

        private void LoadData()
        {
            Pets = _petService.GetPetsByUser(UserID);
            var today = DateTime.Today;
            var reminderWindowEnd = today.AddDays(4);
            var vetItems = _vetVisitService.GetDashboardVisits(UserID, today, reminderWindowEnd);
            foreach (var pet in Pets)
            {
                PetTasks[pet.PetID] = _petService.GetTasksByPetID(pet.PetID, ShowAllTime);
                PetAllTasks[pet.PetID] = ShowAllTime ? PetTasks[pet.PetID] : _petService.GetTasksByPetID(pet.PetID, true);

                var medicationItems = _medicationService.GetScheduleByPetID(pet.PetID, false)
                    .Where(schedule => !schedule.IsConfirmed
                        && schedule.ScheduleDate < reminderWindowEnd)
                    .Select(schedule => new DashboardCareItem
                    {
                        PetID = pet.PetID,
                        DueAt = schedule.ScheduleDate,
                        Kind = "Medication",
                        Text = schedule.MedicationName,
                        Url = $"/Medications?petID={pet.PetID}",
                        IsOverdue = schedule.ScheduleDate.Date < today
                    });

                PetCareItems[pet.PetID] = medicationItems
                    .Concat(vetItems.Where(item => item.PetID == pet.PetID))
                    .OrderBy(item => item.DueAt)
                    .ThenBy(item => item.Kind)
                    .ToList();
            }
        }
    }
}
