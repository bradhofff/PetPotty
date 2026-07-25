using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Services;

namespace PetPotty.Pages
{
    public class VetVisitsModel : PageModel
    {
        public static readonly string[] VisitTypes =
        [
            "Wellness exam", "Vaccination", "Dental", "Surgery", "Emergency",
            "Follow-up", "Lab work", "Specialist", "Other"
        ];

        public static readonly string[] AppointmentStatuses =
            ["Scheduled", "Confirmed", "Completed", "Cancelled", "Missed", "Rescheduled"];

        public static readonly string[] DocumentTypes =
        [
            "Invoice", "Receipt", "Lab result", "Vaccination record", "Prescription",
            "Discharge instructions", "Medical record", "Image", "Other"
        ];

        private readonly IPetService _petService;
        private readonly IVetVisitService _vetVisitService;
        private readonly IVetVisitDocumentStorage _documentStorage;
        private readonly ILogger<VetVisitsModel> _logger;

        public VetVisitsModel(
            IPetService petService,
            IVetVisitService vetVisitService,
            IVetVisitDocumentStorage documentStorage,
            ILogger<VetVisitsModel> logger)
        {
            _petService = petService;
            _vetVisitService = vetVisitService;
            _documentStorage = documentStorage;
            _logger = logger;
        }

        public int UserID { get; set; }
        public int? SelectedPetID { get; set; }
        public int? FocusVisitID { get; set; }
        public bool OpenAddModal { get; set; }
        public string Sort { get; set; } = "Soonest";
        public string? ModalToOpen { get; set; }
        public string? PageError { get; set; }
        public string AddSubmissionToken { get; set; } = Guid.NewGuid().ToString("N");

        public List<Pet> Pets { get; set; } = [];
        public List<VetVisit> Visits { get; set; } = [];
        public List<VetVisit> UpcomingVisits { get; set; } = [];
        public List<VetVisit> PastVisits { get; set; } = [];
        public List<VetVisit> DueReminders { get; set; } = [];
        public Dictionary<int, List<VetVisitDocument>> DocumentsByVisit { get; set; } = [];
        public Dictionary<int, List<VetVisitHistory>> HistoryByVisit { get; set; } = [];

        [BindProperty] public VetVisitInput NewVisit { get; set; } = new();
        [BindProperty] public VetVisitInput EditVisit { get; set; } = new();
        [BindProperty] public CompleteVetVisitInput Completion { get; set; } = new();
        [BindProperty] public IFormFile? CompletionDocument { get; set; }
        [BindProperty] public string CompletionDocumentType { get; set; } = "Medical record";
        [BindProperty] public IFormFile? DocumentUpload { get; set; }
        [BindProperty] public int DocumentVisitID { get; set; }
        [BindProperty] public string DocumentType { get; set; } = "Other";
        [BindProperty] public string DocumentDisplayName { get; set; } = string.Empty;
        [BindProperty] public string DocumentDescription { get; set; } = string.Empty;

        public IActionResult OnGet(int? petID, int? visitID, bool add = false, string sort = "Soonest")
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");

            RemoveModelStatePrefixes(nameof(EditVisit), nameof(Completion));
            UserID = userID;
            Sort = sort is "Newest" or "Oldest" ? sort : "Soonest";
            LoadPageData(petID);
            FocusVisitID = Visits.Any(visit => visit.VetVisitID == visitID) ? visitID : null;
            OpenAddModal = add && petID.HasValue && Pets.Any(pet => pet.PetID == petID.Value);
            if (OpenAddModal)
                NewVisit.PetID = petID!.Value;
            return Page();
        }

        public IActionResult OnPostAddVisit(string submissionToken)
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");

            RemoveModelStatePrefixes(nameof(NewVisit), nameof(Completion));
            UserID = userID;
            LoadPets();
            ValidateVisit(NewVisit, nameof(NewVisit));
            if (string.IsNullOrWhiteSpace(submissionToken))
                ModelState.AddModelError(string.Empty, "This form has expired. Please reopen it and try again.");
            else if (HttpContext.Session.GetString($"vet-visit-submission:{submissionToken}") != null)
            {
                TempData["StatusMessage"] = "That visit was already saved.";
                return RedirectToPage(new { petID = NewVisit.PetID });
            }

            DateTime? reminderAt = null;
            if (ModelState.IsValid)
                reminderAt = CalculateReminder(NewVisit, nameof(NewVisit));
            if (!ModelState.IsValid)
                return ShowModal("addVisitModal", NewVisit.PetID);

            try
            {
                var visitID = _vetVisitService.AddVisit(userID, NewVisit, reminderAt);
                if (visitID == 0)
                    return Forbid();

                HttpContext.Session.SetString($"vet-visit-submission:{submissionToken}", visitID.ToString());
                TempData["StatusMessage"] = "Vet visit added.";
                return RedirectToPage(new { petID = NewVisit.PetID, visitID });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not add a vet visit for user {UserID}", userID);
                ModelState.AddModelError(string.Empty, "The visit could not be saved. Make sure the Vet Visits database migration has been applied.");
                return ShowModal("addVisitModal", NewVisit.PetID);
            }
        }

        public IActionResult OnPostEditVisit()
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");

            UserID = userID;
            LoadPets();
            if (_vetVisitService.GetVisit(userID, EditVisit.VetVisitID) == null)
                return Forbid();

            ValidateVisit(EditVisit, nameof(EditVisit));
            DateTime? reminderAt = null;
            if (ModelState.IsValid)
                reminderAt = CalculateReminder(EditVisit, nameof(EditVisit));
            if (!ModelState.IsValid)
                return ShowModal("editVisitModal", EditVisit.PetID);

            try
            {
                if (!_vetVisitService.UpdateVisit(userID, EditVisit, reminderAt))
                    return Forbid();

                TempData["StatusMessage"] = "Vet visit updated.";
                return RedirectToPage(new { petID = EditVisit.PetID, visitID = EditVisit.VetVisitID });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not update vet visit {VetVisitID}", EditVisit.VetVisitID);
                ModelState.AddModelError(string.Empty, "The visit could not be updated. Please try again.");
                return ShowModal("editVisitModal", EditVisit.PetID);
            }
        }

        public IActionResult OnPostChangeStatus(int vetVisitID, string status, string? details)
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");
            if (!AppointmentStatuses.Contains(status, StringComparer.OrdinalIgnoreCase) || status == "Completed")
                return BadRequest();

            var visit = _vetVisitService.GetVisit(userID, vetVisitID);
            if (visit == null)
                return Forbid();
            if (!_vetVisitService.ChangeStatus(userID, vetVisitID, status, details ?? string.Empty))
                return Forbid();

            TempData["StatusMessage"] = status == "Cancelled"
                ? "Appointment cancelled; its history was preserved."
                : $"Appointment marked {status.ToLowerInvariant()}.";
            return RedirectToPage(new { petID = visit.PetID, visitID = vetVisitID });
        }

        public async Task<IActionResult> OnPostCompleteVisitAsync()
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");

            var visit = _vetVisitService.GetVisit(userID, Completion.VetVisitID);
            if (visit == null)
                return Forbid();
            RemoveModelStatePrefixes(nameof(NewVisit), nameof(EditVisit));
            if (Completion.FollowUpDate.HasValue && Completion.FollowUpDate.Value.Date < visit.VisitDate.Date)
                ModelState.AddModelError("Completion.FollowUpDate", "Follow-up date cannot be earlier than the visit.");
            if (CompletionDocument is { Length: > 0 })
            {
                var documentError = _documentStorage.Validate(CompletionDocument);
                if (documentError != null)
                    ModelState.AddModelError(nameof(CompletionDocument), documentError);
                if (!DocumentTypes.Contains(CompletionDocumentType, StringComparer.OrdinalIgnoreCase))
                    ModelState.AddModelError(nameof(CompletionDocumentType), "Choose a supported document type.");
            }
            if (!ModelState.IsValid)
            {
                UserID = userID;
                return ShowModal("completeVisitModal", visit.PetID);
            }

            if (!_vetVisitService.CompleteVisit(userID, Completion))
                return Forbid();

            if (CompletionDocument is { Length: > 0 })
            {
                try
                {
                    await SaveDocumentAsync(
                        userID, visit, CompletionDocument, CompletionDocumentType,
                        Path.GetFileNameWithoutExtension(SafeOriginalFileName(CompletionDocument.FileName)),
                        "Added while completing the visit.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Visit {VetVisitID} completed, but its document could not be saved", visit.VetVisitID);
                    TempData["StatusMessage"] = "Visit completed, but the document could not be saved. You can attach it from visit details.";
                    return RedirectToPage(new { petID = visit.PetID, visitID = visit.VetVisitID });
                }
            }

            TempData["StatusMessage"] = "Visit completed and medical details saved.";
            return RedirectToPage(new { petID = visit.PetID, visitID = visit.VetVisitID });
        }

        public IActionResult OnPostDeleteVisit(int vetVisitID)
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");

            var visit = _vetVisitService.GetVisit(userID, vetVisitID);
            if (visit == null)
                return Forbid();

            if (_vetVisitService.DeleteVisit(userID, vetVisitID))
                TempData["StatusMessage"] = "Visit deleted.";
            else
                TempData["StatusMessage"] = "Completed visits or visits with documents cannot be deleted. Cancel the appointment or remove its documents first.";
            return RedirectToPage(new { petID = visit.PetID });
        }

        public IActionResult OnPostDismissReminder(int reminderID, int petID)
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");
            if (!_vetVisitService.DismissReminder(userID, reminderID))
                return Forbid();

            TempData["StatusMessage"] = "Reminder dismissed.";
            return RedirectToPage(new { petID });
        }

        public async Task<IActionResult> OnPostUploadDocumentAsync()
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");

            var visit = _vetVisitService.GetVisit(userID, DocumentVisitID);
            if (visit == null)
                return Forbid();

            var error = _documentStorage.Validate(DocumentUpload);
            if (!DocumentTypes.Contains(DocumentType, StringComparer.OrdinalIgnoreCase))
                error = "Choose a supported document type.";
            if (DocumentDisplayName.Length > 260 || DocumentDescription.Length > 1000)
                error = "The document name or description is too long.";
            if (error != null)
            {
                TempData["StatusMessage"] = $"Document not uploaded: {error}";
                return RedirectToPage(new { petID = visit.PetID, visitID = visit.VetVisitID });
            }

            try
            {
                await SaveDocumentAsync(
                    userID, visit, DocumentUpload!, DocumentType,
                    DocumentDisplayName, DocumentDescription);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not upload a document for vet visit {VetVisitID}", visit.VetVisitID);
                TempData["StatusMessage"] = "The document could not be saved.";
                return RedirectToPage(new { petID = visit.PetID, visitID = visit.VetVisitID });
            }

            TempData["StatusMessage"] = "Document uploaded.";
            return RedirectToPage(new { petID = visit.PetID, visitID = visit.VetVisitID });
        }

        public IActionResult OnGetDownloadDocument(int documentID)
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");

            var document = _vetVisitService.GetDocument(userID, documentID);
            if (document == null)
                return Forbid();
            var physicalPath = _documentStorage.ResolvePhysicalPath(document.StoredPath);
            if (physicalPath == null || !System.IO.File.Exists(physicalPath))
                return NotFound("The document record exists, but its stored file is missing.");

            var downloadName = Path.GetFileName(document.OriginalFileName);
            return new PhysicalFileResult(physicalPath, document.ContentType)
            {
                FileDownloadName = downloadName,
                EnableRangeProcessing = true
            };
        }

        public IActionResult OnPostUpdateDocument(
            int documentID, string documentType, string displayName, string? description)
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");

            var document = _vetVisitService.GetDocument(userID, documentID);
            if (document == null)
                return Forbid();
            if (!DocumentTypes.Contains(documentType, StringComparer.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(displayName)
                || displayName.Length > 260
                || (description?.Length ?? 0) > 1000)
            {
                TempData["StatusMessage"] = "Document details were not valid.";
                return RedirectToPage(new { visitID = document.VetVisitID });
            }

            if (!_vetVisitService.UpdateDocument(userID, documentID, documentType, displayName, description ?? string.Empty))
                return Forbid();
            TempData["StatusMessage"] = "Document details updated.";
            return RedirectToVisit(userID, document.VetVisitID);
        }

        public IActionResult OnPostDeleteDocument(int documentID)
        {
            if (!TryGetUserID(out var userID))
                return RedirectToPage("/Login");

            var document = _vetVisitService.DeleteDocument(userID, documentID);
            if (document == null)
                return Forbid();
            _documentStorage.Delete(document.StoredPath);
            TempData["StatusMessage"] = "Document removed from the visit and storage.";
            return RedirectToVisit(userID, document.VetVisitID);
        }

        public static string AppointmentTimeLabel(VetVisit visit)
        {
            if (visit.IsAllDay)
                return "All day";
            return visit.VisitTime.HasValue
                ? DateTime.Today.Add(visit.VisitTime.Value).ToString("h:mm tt")
                : "Time TBD";
        }

        public static string ReminderLabel(VetVisit visit)
        {
            if (!visit.ReminderAt.HasValue)
                return string.Empty;
            var day = visit.ReminderAt.Value.Date == DateTime.Today
                ? "today"
                : visit.ReminderAt.Value.ToString("MMM d");
            return $"{visit.PetName}'s {visit.VisitType.ToLowerInvariant()} visit is {RelativeDay(visit.VisitDate)} ({AppointmentTimeLabel(visit)}). Reminder set for {day} at {visit.ReminderAt.Value:h:mm tt}.";
        }

        public static string RelativeDay(DateTime value)
        {
            var days = (value.Date - DateTime.Today).Days;
            return days switch
            {
                < 0 => $"{Math.Abs(days)} day{(Math.Abs(days) == 1 ? "" : "s")} ago",
                0 => "today",
                1 => "tomorrow",
                _ => $"in {days} days"
            };
        }

        private void LoadPageData(int? requestedPetID)
        {
            LoadPets();
            SelectedPetID = requestedPetID.HasValue && Pets.Any(pet => pet.PetID == requestedPetID.Value)
                ? requestedPetID
                : null;
            Visits = _vetVisitService.GetVisits(UserID, SelectedPetID);

            var activeStatuses = new[] { "Scheduled", "Confirmed", "Rescheduled" };
            UpcomingVisits = Visits
                .Where(visit => activeStatuses.Contains(visit.Status) && visit.VisitDate.Date >= DateTime.Today)
                .OrderBy(visit => visit.VisitDate)
                .ThenBy(visit => visit.IsAllDay ? TimeSpan.Zero : visit.VisitTime ?? TimeSpan.MaxValue)
                .ToList();

            var upcomingIDs = UpcomingVisits.Select(visit => visit.VetVisitID).ToHashSet();
            PastVisits = Visits.Where(visit => !upcomingIDs.Contains(visit.VetVisitID)).ToList();
            PastVisits = Sort switch
            {
                "Oldest" => PastVisits.OrderBy(visit => visit.VisitDate).ThenBy(visit => visit.VisitTime).ToList(),
                _ => PastVisits.OrderByDescending(visit => visit.VisitDate).ThenByDescending(visit => visit.VisitTime).ToList()
            };

            DueReminders = UpcomingVisits
                .Where(visit => visit.ReminderAt <= DateTime.Now
                    && visit.ReminderStatus == "Displayed"
                    && visit.ReminderID.HasValue)
                .OrderBy(visit => visit.AppointmentDateTime)
                .ToList();

            foreach (var visit in Visits)
            {
                DocumentsByVisit[visit.VetVisitID] = _vetVisitService.GetDocuments(UserID, visit.VetVisitID);
                HistoryByVisit[visit.VetVisitID] = _vetVisitService.GetHistory(UserID, visit.VetVisitID);
            }

            if (NewVisit.PetID == 0 && SelectedPetID.HasValue)
                NewVisit.PetID = SelectedPetID.Value;
        }

        private void LoadPets() => Pets = _petService.GetPetsByUser(UserID);

        private PageResult ShowModal(string modalID, int? petID)
        {
            ModalToOpen = modalID;
            LoadPageData(petID);
            return Page();
        }

        private void ValidateVisit(VetVisitInput visit, string prefix)
        {
            if (Pets.All(pet => pet.PetID != visit.PetID))
                ModelState.AddModelError($"{prefix}.PetID", "Choose one of your pets.");
            if (!VisitTypes.Contains(visit.VisitType, StringComparer.OrdinalIgnoreCase))
                ModelState.AddModelError($"{prefix}.VisitType", "Choose a supported visit type.");
            if (!AppointmentStatuses.Contains(visit.Status, StringComparer.OrdinalIgnoreCase))
                ModelState.AddModelError($"{prefix}.Status", "Choose a supported status.");
            if (prefix == nameof(NewVisit)
                && string.Equals(visit.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                ModelState.AddModelError($"{prefix}.Status", "Use the Complete Visit workflow to record a completed appointment.");
            if (visit.FollowUpDate.HasValue && visit.FollowUpDate.Value.Date < visit.VisitDate.Date)
                ModelState.AddModelError($"{prefix}.FollowUpDate", "Follow-up date cannot be earlier than the visit.");

            var isActive = visit.Status is "Scheduled" or "Confirmed" or "Rescheduled";
            if (isActive && visit.VisitDate.Date < DateTime.Today && !visit.IsEmergency)
                ModelState.AddModelError($"{prefix}.VisitDate", "A scheduled visit cannot be in the past unless it is an emergency record.");
        }

        private DateTime? CalculateReminder(VetVisitInput visit, string prefix)
        {
            if (visit.ReminderChoice == "None" || visit.Status is "Completed" or "Cancelled" or "Missed")
                return null;

            var appointment = visit.VisitDate.Date
                + (visit.IsAllDay ? TimeSpan.FromHours(9) : visit.VisitTime ?? TimeSpan.FromHours(9));
            var reminder = visit.ReminderChoice switch
            {
                "SameDay" => appointment.TimeOfDay > TimeSpan.FromHours(8)
                    ? visit.VisitDate.Date.AddHours(8)
                    : appointment.AddHours(-1),
                "OneDay" => appointment.AddDays(-1),
                "ThreeDays" => appointment.AddDays(-3),
                "OneWeek" => appointment.AddDays(-7),
                "Custom" => visit.CustomReminderAt,
                _ => null
            };

            if (!reminder.HasValue)
                ModelState.AddModelError($"{prefix}.CustomReminderAt", "Choose a reminder time.");
            else if (reminder.Value >= appointment)
                ModelState.AddModelError($"{prefix}.CustomReminderAt", "Reminder must be before the appointment.");
            return reminder;
        }

        private RedirectToPageResult RedirectToVisit(int userID, int visitID)
        {
            var visit = _vetVisitService.GetVisit(userID, visitID);
            return RedirectToPage(new { petID = visit?.PetID, visitID });
        }

        private async Task SaveDocumentAsync(
            int userID, VetVisit visit, IFormFile upload, string documentType,
            string displayName, string description)
        {
            string? storedPath = null;
            try
            {
                storedPath = await _documentStorage.SaveAsync(
                    visit.VetVisitID, upload, HttpContext.RequestAborted);
                var originalName = SafeOriginalFileName(upload.FileName);
                var documentID = _vetVisitService.AddDocument(userID, new VetVisitDocument
                {
                    VetVisitID = visit.VetVisitID,
                    DocumentType = documentType,
                    DisplayName = string.IsNullOrWhiteSpace(displayName)
                        ? Path.GetFileNameWithoutExtension(originalName)
                        : displayName,
                    OriginalFileName = originalName,
                    StoredPath = storedPath,
                    ContentType = upload.ContentType,
                    FileSizeBytes = upload.Length,
                    Description = description
                });
                if (documentID == 0)
                    throw new InvalidOperationException("The document record was not created.");
            }
            catch
            {
                _documentStorage.Delete(storedPath);
                throw;
            }
        }

        private bool TryGetUserID(out int userID) =>
            int.TryParse(HttpContext.Session.GetString("userID"), out userID);

        private static string SafeOriginalFileName(string fileName)
        {
            var leafName = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
            var safeName = new string(leafName
                .Where(character => !char.IsControl(character) && character is not '/' and not '\\')
                .ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "document";
            return safeName.Length <= 240 ? safeName : safeName[..240];
        }

        private void RemoveModelStatePrefixes(params string[] prefixes)
        {
            var keys = ModelState.Keys
                .Where(key => prefixes.Any(prefix =>
                    key.Equals(prefix, StringComparison.Ordinal)
                    || key.StartsWith($"{prefix}.", StringComparison.Ordinal)))
                .ToList();
            foreach (var key in keys)
                ModelState.Remove(key);
        }
    }
}
