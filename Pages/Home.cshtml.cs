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

        [BindProperty(SupportsGet = true)]
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
        [BindProperty] public int UpdateTaskID { get; set; }
        [BindProperty] public string UpdateTaskType { get; set; } = string.Empty;
        [BindProperty] public string UpdateTaskNotes { get; set; } = string.Empty;
        [BindProperty] public DateTime UpdateTaskCreatedAt { get; set; } = DateTime.Now;

        public string StatusMessage { get; set; } = string.Empty;

        // ============================================================
        // GET
        // ============================================================
        public IActionResult OnGet()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            UserName = HttpContext.Session.GetString("name") ?? string.Empty;

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
        // Add Task
        // ============================================================
        public IActionResult OnPostAddTask()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            _petService.AddTask(NewTaskPetID, NewTaskType, NewTaskNotes, NewTaskCreatedAt);

            TempData["StatusMessage"] = "Task added successfully!";
            return RedirectToPage();
        }

        // ============================================================
        // Delete Task
        // taskID comes from asp-route-taskID on the form in the Razor view
        // so it's a method parameter, not a [BindProperty]
        // ============================================================
        public IActionResult OnPostDeleteTask(int taskID)
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            _petService.DeleteTask(taskID);

            TempData["StatusMessage"] = "Task deleted.";
            return RedirectToPage(new { showAllTime = ShowAllTime });
        }

        // ============================================================
        // Update Task
        // ============================================================
        public IActionResult OnPostUpdateTask()
        {
            if (!int.TryParse(HttpContext.Session.GetString("userID"), out int userID))
                return RedirectToPage("/Login");

            UserID = userID;
            _petService.UpdateTask(UpdateTaskID, UpdateTaskType, UpdateTaskNotes, UpdateTaskCreatedAt);

            TempData["StatusMessage"] = "Task updated successfully!";
            return RedirectToPage(new { showAllTime = ShowAllTime });
        }

        // ============================================================
        // Private helpers
        // ============================================================
        private void LoadData()
        {
            Pets = _petService.GetPetsByUser(UserID);
            
            foreach (var pet in Pets)
            {
                PetTasks[pet.PetID] = _petService.GetTasksByPetID(pet.PetID, ShowAllTime);
            }
        }
    }
}
