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
        private readonly IUserTimeZoneService _timeZone;
        private readonly IHouseholdContextService _householdContext;
        private readonly ILogger<HomeModel> _logger;

        // Razor Pages constructs this model through DI for each request. Program.cs maps
        // interfaces to SQL services and filesystem storage; the view never opens SQL.
        public HomeModel(
            IPetService petService,
            IMedicationService medicationService,
            IVetVisitService vetVisitService,
            IPetImageStorage petImageStorage,
            IVetVisitDocumentStorage vetVisitDocumentStorage,
            IUserTimeZoneService timeZone,
            IHouseholdContextService householdContext,
            ILogger<HomeModel> logger)
        {
            _petService = petService;
            _medicationService = medicationService;
            _vetVisitService = vetVisitService;
            _petImageStorage = petImageStorage;
            _vetVisitDocumentStorage = vetVisitDocumentStorage;
            _timeZone = timeZone;
            _householdContext = householdContext;
            _logger = logger;
        }

        public string UserName { get; set; } = string.Empty;
        public int UserID { get; set; }
        // View data: pet cards, visible history, latest potty activity, and care reminders.
        // Despite its name, PetAllTasks holds only the latest matching Pee/Poop records.
        public List<Pet> Pets { get; set; } = new();
        public Dictionary<int, List<TaskItem>> PetTasks { get; set; } = new();
        public Dictionary<int, List<TaskItem>> PetAllTasks { get; set; } = new();
        public Dictionary<int, List<DashboardCareItem>> PetCareItems { get; set; } = new();
        public DateTime UserNowLocal { get; set; }
        public bool CanManagePets { get; set; }

        public bool ShowAllTime { get; set; } = false;
        public int TaskHistoryStage { get; set; }
        public bool HasOlderTasks { get; set; }
        public string TaskHistoryRangeLabel => TaskHistoryStage switch
        {
            0 => "Showing the last 7 days",
            1 => "Showing the last 14 days",
            2 => "Showing the last month",
            3 => "Showing the last 6 months",
            4 => "Showing the last year",
            5 => "Showing the last 2 years",
            6 => "Showing the last 5 years",
            7 => "Showing the last 10 years",
            _ => "Showing all available history"
        };
        public string ShowMoreTaskHistoryLabel => TaskHistoryStage switch
        {
            0 => "Show previous week",
            1 => "Show previous month",
            2 => "Show previous 6 months",
            3 => "Show previous year",
            4 => "Show previous 2 years",
            5 => "Show previous 5 years",
            6 => "Show previous 10 years",
            _ => "Show all remaining records"
        };
        public string? PetImageError { get; set; }
        public string? ModalToOpen { get; set; }
        public string? EditPetCurrentImagePath { get; set; }

        // BindProperty maps form inputs to values; it does not establish ownership or
        // replace server-side validation. Each asp-page-handler selects an OnPost method.
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

            // Split from the image-save step below so a failure here is never reported to
            // the user as a photo problem — no photo has been touched yet at this point.
            int petID;
            try
            {
                petID = _petService.AddPet(UserID, NewPetName, NewPetType, NewPetBreed,
                                           NewPetAge, NewPetBirthdate, NewPetGender);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not add pet for user {UserID}", UserID);
                return ShowPetModalError("addPetModal", "The pet could not be added. Please check the details and try again.");
            }

            if (NewPetImage != null && NewPetImage.Length > 0)
            {
                string? savedImagePath = null;
                try
                {
                    savedImagePath = await _petImageStorage.SaveAsync(petID, NewPetImage, HttpContext.RequestAborted);
                    _petService.UpdatePetProfileImagePath(UserID, petID, savedImagePath);
                }
                catch (Exception ex)
                {
                    _petImageStorage.Delete(savedImagePath);
                    try { _petService.DeletePet(UserID, petID); }
                    catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "Could not roll back pet {PetID} after image upload failed", petID); }

                    _logger.LogError(ex, "Could not save profile image for pet {PetID} (user {UserID})", petID, UserID);
                    return ShowPetModalError("addPetModal", "The pet photo could not be saved. Please try again.");
                }
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
                return NotFound();

            EditPetCurrentImagePath = pet.ProfileImagePath;
            var validationError = _petImageStorage.Validate(EditPetImage);
            if (validationError != null)
                return ShowPetModalError("editPetModal", validationError);

            string? newImagePath = null;
            try
            {
                if (EditPetImage != null && EditPetImage.Length > 0)
                    newImagePath = await _petImageStorage.SaveAsync(EditPetID, EditPetImage, HttpContext.RequestAborted);

                if (!_petService.EditPet(UserID, EditPetID, EditPetName, EditPetType, EditPetBreed,
                                    EditPetAge, EditPetBirthdate, EditPetGender))
                {
                    _petImageStorage.Delete(newImagePath);
                    return NotFound();
                }

                if (newImagePath != null)
                {
                    if (!_petService.UpdatePetProfileImagePath(UserID, EditPetID, newImagePath))
                    {
                        _petImageStorage.Delete(newImagePath);
                        return NotFound();
                    }
                    _petImageStorage.Delete(pet.ProfileImagePath);
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
                return NotFound();

            var documentPaths = _vetVisitService.GetDocumentPathsByPet(UserID, EditPetID);
            if (!_petService.DeletePet(UserID, EditPetID))
                return NotFound();
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
                return NotFound();

            // Clear the database reference first so a filesystem cleanup issue
            // can never leave the UI pointing at a missing image.
            if (!_petService.UpdatePetProfileImagePath(UserID, EditPetID, null))
                return NotFound();
            _petImageStorage.Delete(pet.ProfileImagePath);

            TempData["StatusMessage"] = $"{pet.Name}'s profile picture was reset.";
            return RedirectToPage();
        }

        // ============================================================
        // Quick Log — Pee or Poop at the client-local time selected in the popup
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
            if (!_petService.AddTask(UserID, petID, taskType, string.Empty, timestamp))
                return NotFound();

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
            HttpContext.Session.SetInt32("homeTaskHistoryStage", 0);
            return LocalRedirect("/Home#task-log");
        }

        public IActionResult OnPostShowMoreTasks()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            var nextStage = Math.Min(GetTaskHistoryStage() + 1, 8);
            HttpContext.Session.SetString("homeShowAllTime", bool.TrueString);
            HttpContext.Session.SetInt32("homeTaskHistoryStage", nextStage);
            return LocalRedirect("/Home#task-log");
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
            if (!_petService.AddTask(UserID, NewTaskPetID, NewTaskType, NewTaskNotes, NewTaskCreatedAt))
                return NotFound();

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
            if (!_petService.UpdateTask(UserID, UpdateTaskID, UpdateTaskType, UpdateTaskNotes, UpdateTaskCreatedAt))
                return NotFound();

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
            if (!_petService.DeleteTask(UserID, taskID))
                return NotFound();

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

        public static string LastActivityLabel(List<TaskItem> tasks, string taskType, DateTime userNowLocal)
        {
            if (tasks.Count == 0)
                return "No tasks found";

            var lastTask = tasks
                .Where(task => IsActivityMatch(task.TaskType, taskType))
                .OrderByDescending(task => task.CreatedAt)
                .FirstOrDefault();

            if (lastTask == null)
                return $"No {taskType.ToLower()} found";

            var elapsed = userNowLocal - lastTask.CreatedAt;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            if (elapsed.TotalDays > 7)
                return $"Last {taskType.ToLower()}: More than a week ago...";

            var timeText = lastTask.CreatedAt.ToString("h:mm tt");
            if (lastTask.CreatedAt.Date != userNowLocal.Date)
                timeText = $"{ShortWeekday(lastTask.CreatedAt.DayOfWeek)} @ {timeText}";

            return $"Last {taskType.ToLower()}: {timeText}";
        }

        private static string ShortWeekday(DayOfWeek day) => day switch
        {
            DayOfWeek.Sunday => "Sun",
            DayOfWeek.Monday => "Mon",
            DayOfWeek.Tuesday => "Tues",
            DayOfWeek.Wednesday => "Wed",
            DayOfWeek.Thursday => "Thurs",
            DayOfWeek.Friday => "Fri",
            DayOfWeek.Saturday => "Sat",
            _ => string.Empty
        };

        public static string CareItemLabel(DashboardCareItem item, DateTime userTodayLocal)
        {
            var days = (item.DueAt.Date - userTodayLocal.Date).Days;
            var dayLabel = days switch
            {
                < -1 => $"was due {-days} days ago",
                -1 => "was due yesterday",
                0 => "due today",
                1 => "due tomorrow",
                _ => $"due in {days} days"
            };
            var timeLabel = item.DueAt.ToString("h:mm tt");

            if (item.Kind == "Medication" && item.IsOverdue)
            {
                var daysOverdue = Math.Max(1, -days);
                var dayUnit = daysOverdue == 1 ? "day" : "days";
                return $"Past due: {item.Text} was due {daysOverdue} {dayUnit} ago";
            }

            if (item.Kind == "Medication")
                return item.TimingDoesNotMatter
                    ? $"{item.Text} {dayLabel}"
                    : $"{item.Text} {dayLabel} at {timeLabel}";

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

        private int GetTaskHistoryStage()
        {
            return Math.Clamp(HttpContext.Session.GetInt32("homeTaskHistoryStage") ?? 0, 0, 8);
        }

        private static DateTime GetTaskHistoryStartDate(int stage, DateTime userNowLocal)
        {
            var now = userNowLocal;
            return stage switch
            {
                0 => now.AddDays(-7),
                1 => now.AddDays(-14),
                2 => now.AddMonths(-1),
                3 => now.AddMonths(-6),
                4 => now.AddYears(-1),
                5 => now.AddYears(-2),
                6 => now.AddYears(-5),
                7 => now.AddYears(-10),
                _ => new DateTime(1753, 1, 1)
            };
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

        // Assemble the dashboard. Session stores history preferences; reminder ranges
        // and the latest Pee/Poop labels are independent of the selected task-history range.
        private void LoadData()
        {
            var household = _householdContext.GetActiveHousehold(UserID);
            CanManagePets = household != null
                && HouseholdAccessRules.HasPermission(household.Role, HouseholdPermission.ManagePets);
            Pets = _petService.GetPetsByUser(UserID);
            var utcOffsetMinutes = _timeZone.GetUtcOffsetMinutes(Request);
            UserNowLocal = _timeZone.ToLocal(DateTime.UtcNow, utcOffsetMinutes);
            TaskHistoryStage = GetTaskHistoryStage();
            HasOlderTasks = false;
            var taskHistoryStartDate = GetTaskHistoryStartDate(TaskHistoryStage, UserNowLocal);
            var today = UserNowLocal.Date;
            var reminderWindowEnd = today.AddDays(4);
            var vetReminderWindowEnd = today.AddDays(7);
            var vetItems = _vetVisitService.GetDashboardVisits(UserID, today, vetReminderWindowEnd);
            foreach (var pet in Pets)
            {
                if (ShowAllTime)
                {
                    PetTasks[pet.PetID] = _petService.GetTasksByPetIDSince(
                        UserID,
                        pet.PetID,
                        taskHistoryStartDate,
                        out var petHasOlderTasks);
                    HasOlderTasks |= petHasOlderTasks;
                }
                else
                {
                    PetTasks[pet.PetID] = _petService.GetTasksByPetID(UserID, pet.PetID, false);
                }

                PetAllTasks[pet.PetID] = _petService.GetLatestActivityTasksByPetID(UserID, pet.PetID);

                var medicationItems = _medicationService.GetScheduleByPetID(UserID, pet.PetID, false, utcOffsetMinutes)
                    .Where(schedule => !schedule.IsResolved
                        && schedule.ScheduleDate < reminderWindowEnd)
                    .Select(schedule => new DashboardCareItem
                    {
                        PetID = pet.PetID,
                        DueAt = schedule.ScheduleDate,
                        Kind = "Medication",
                        Text = schedule.MedicationName,
                        Url = $"/Medications?petID={pet.PetID}",
                        IsOverdue = schedule.TimingDoesNotMatter
                            ? schedule.ScheduleDate.Date < today
                            : schedule.ScheduleDate < UserNowLocal,
                        TimingDoesNotMatter = schedule.TimingDoesNotMatter
                    });

                // Home.cshtml displays the first three reminders and counts the remainder.
                PetCareItems[pet.PetID] = medicationItems
                    .Concat(vetItems.Where(item => item.PetID == pet.PetID))
                    .OrderBy(item => item.DueAt)
                    .ThenBy(item => item.Kind)
                    .ToList();
            }
        }
    }
}
