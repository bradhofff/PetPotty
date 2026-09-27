using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetPotty.Models;
using PetPotty.Pages;
using PetPotty.Services;

// Offline capture host only. Uses the production Razor views with fictional data.
// No database, credentials, production handlers, or write requests are available.
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    ContentRootPath = root, WebRootPath = Path.Combine(root, "wwwroot"),
    ApplicationName = typeof(HomeModel).Assembly.GetName().Name
});
builder.WebHost.UseUrls("http://127.0.0.1:5079");
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession();
builder.Services.AddRazorPages(options => options.Conventions.ConfigureFilter(new CaptureData()))
    .AddApplicationPart(typeof(HomeModel).Assembly);
foreach (var service in typeof(IPetService).Assembly.GetTypes().Where(t => t.IsInterface && t.Namespace == "PetPotty.Services"))
    builder.Services.AddSingleton(service, DispatchProxy.Create(service, typeof(NoDataAccess)));
builder.Services.AddSingleton<IUserTimeZoneService, BrowserTimeZoneService>();
var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.Use(async (context, next) => {
    if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }
    if (context.Request.Path != "/") {
        context.Session.SetString("userID", "1");
        context.Session.SetString("name", "Alex");
    } else context.Session.Clear();
    await next();
});
app.MapRazorPages();
app.Run();

public class NoDataAccess : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        throw new InvalidOperationException("Capture host cannot access services: " + method?.Name);
}

public sealed class CaptureData : IPageFilter
{
    public void OnPageHandlerSelected(PageHandlerSelectedContext context) { }
    public void OnPageHandlerExecuted(PageHandlerExecutedContext context) { }
    public void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        var today = DateTime.Today;
        List<Pet> pets = [
            new() { PetID = 1, Name = "Scout", Type = "Dog", Breed = "Golden Retriever", Age = "3", Gender = "Male", Birthdate = today.AddYears(-3) },
            new() { PetID = 2, Name = "Juniper", Type = "Cat", Breed = "Domestic Shorthair", Age = "2", Gender = "Female", Birthdate = today.AddYears(-2) }
        ];
        List<Medication> meds = [new() { MedID = 1, PetID = 1, MedicationName = "Daily supplement", Dosage = "As prescribed", FrequencyType = "Daily", FrequencyInterval = 1, StartDate = today.AddDays(-20), TimingDoesNotMatter = true, Notes = "Give with breakfast, per vet instructions." }];
        List<MedSchedule> doses = Enumerable.Range(0, 5).Select(i => new MedSchedule {
            ScheduleID = i + 1, MedID = 1, MedicationName = "Daily supplement", Dosage = "As prescribed", FrequencyType = "Daily",
            TimingDoesNotMatter = true, ScheduleDate = today.AddDays(2 - i), IsConfirmed = i > 1,
            DoseStatus = i > 1 ? "Taken" : "Due", EffectiveStatus = i > 1 ? "Taken" : "Due", RecordedByName = i > 1 ? "Alex" : ""
        }).ToList();
        var visit = new VetVisit { VetVisitID = 1, PetID = 1, PetName = "Scout", VisitDate = today.AddDays(5), VisitTime = new TimeSpan(10, 30, 0), ClinicName = "Maple Veterinary Clinic", VeterinarianName = "Dr. Morgan", VisitReason = "Annual checkup", VisitType = "Wellness exam", Status = "Confirmed", Notes = "Bring recent care history and questions.", CreatedByName = "Alex" };
        var past = new VetVisit { VetVisitID = 2, PetID = 2, PetName = "Juniper", VisitDate = today.AddDays(-14), ClinicName = "Maple Veterinary Clinic", VeterinarianName = "Dr. Morgan", VisitReason = "Routine wellness visit", VisitType = "Wellness exam", Status = "Completed", VisitSummary = "Routine exam complete. Continue current care plan." };
        List<HealthTimelineItem> timeline = [new() { SourceID = 1, PetID = 1, PetName = "Scout", SourceType = "Symptom", Title = "Itching", Summary = "Mild scratching after the afternoon walk. Settled by evening.", EventAtLocal = today.AddDays(-1).AddHours(17), EventAtUtc = today.AddDays(-1).AddHours(21), Attribution = "Alex", Severity = 1, Status = "Recovered" }];
        switch (context.HandlerInstance)
        {
            case HomeModel home:
                home.UserName = "Alex"; home.Pets = pets; home.CanManagePets = true; home.UserNowLocal = today.AddHours(9);
                foreach (var pet in pets) {
                    var tasks = new List<TaskItem> {
                        new() { TaskID = pet.PetID * 10, PetID = pet.PetID, PetName = pet.Name, TaskType = "Pee", CreatedAt = today.AddHours(8), RecordedByName = "Alex", Notes = "Morning routine" },
                        new() { TaskID = pet.PetID * 10 + 1, PetID = pet.PetID, PetName = pet.Name, TaskType = "Poop", CreatedAt = today.AddHours(7.5), RecordedByName = "Sam" }
                    };
                    home.PetTasks[pet.PetID] = tasks; home.PetAllTasks[pet.PetID] = tasks;
                }
                break;
            case HealthModel health:
                health.Pets = pets; health.PetID = 1; health.From = today.AddDays(-29); health.To = today; health.CanManageCarePlans = true;
                health.CurrentMedications = [new() { MedID = 1, PetID = 1, PetName = "Scout", MedicationName = "Daily supplement", Dosage = "As prescribed", FrequencyDisplay = "Every day", LastDoseStatus = "Taken", LastDoseWhen = "Today, 8:00 AM", NextDoseWhen = "Tomorrow" }];
                health.MedicationOptions = meds; health.NextVetVisit = visit; health.RecentAdherence = new() { Total = 20, Taken = 20 };
                health.RecentHealthEventCount = 1; health.Timeline = timeline; health.SymptomTimeline = timeline;
                break;
            case MedicationsModel medications:
                medications.Pets = pets; medications.SelectedPetID = 1; medications.Medications = meds; medications.Schedule = doses; medications.CanManageCarePlans = true;
                break;
            case VetVisitsModel visits:
                visits.Pets = pets; visits.SelectedPetID = 1; visits.Visits = [visit]; visits.UpcomingVisits = [visit]; visits.CanManageCarePlans = true;
                break;
            case ReportsModel reports:
                reports.Pets = pets; reports.SelectedPetID = 1; reports.SelectedPet = pets[0]; reports.CurrentMedications = meds;
                reports.DoseRecords = doses.Where(d => d.IsResolved).ToList(); reports.Adherence = new() { Total = 20, Taken = 20 };
                reports.RangeStartLocal = today.AddDays(-29); reports.RangeEndLocal = today;
                reports.QuestionsForVet = "Review Scout’s daily routine and recent itching after walks.";
                break;
            case IndexModel:
                break;
            default:
                context.Result = new NotFoundResult(); return;
        }
        context.Result = new PageResult();
    }
}
