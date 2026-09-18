// ============================================================
// Program.cs — The entire app startup in .NET Core (6+)
// In .NET Framework this was split across:
//   Global.asax, Startup.cs, WebApiConfig.cs, FilterConfig.cs etc.
// Now it's all here in one place using the "minimal hosting model"
// ============================================================

using Microsoft.AspNetCore.Authentication.Cookies;
using PetPotty.Services;

var builder = WebApplication.CreateBuilder(args);

// --------------------
// Services (Dependency Injection registrations)
// "builder.Services" is the DI container — everything registered
// here can be injected into any PageModel or class via constructor
// --------------------

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();

// REQUIRED for session to work — stores session data in memory.
// In production you'd swap this for Redis or SQL-backed sessions.
// Session requires IDistributedCache. This implementation loses its contents on app restart.
builder.Services.AddDistributedMemoryCache();

// Session configuration — replaces System.Web.SessionState from .NET Framework
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromDays(30);   // Keep sessions alive for 30 days of inactivity
    options.Cookie.HttpOnly = true;                 // JS cannot access this cookie (XSS protection)
    options.Cookie.IsEssential = true;              // Essential under ASP.NET's cookie consent policy
    options.Cookie.MaxAge = TimeSpan.FromDays(30);  // Make session cookie persistent (survives browser close)
});

// Cookie authentication — modern replacement for FormsAuthentication from .NET Framework.
// When a page requires auth and user isn't logged in, they get sent to /Login automatically.
// Current Login/Home code uses session userID; Login does not call SignInAsync.
// Registering cookie authentication alone does not sign users into a ClaimsPrincipal.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);       // Stay logged in for 30 days
        options.SlidingExpiration = true;                      // Renew eligible tickets after half their lifetime
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // HTTP ok for local dev, HTTPS on VPS
    });

builder.Services.AddAuthorization();

// Register our data service — AddScoped means one instance per HTTP request.
// Any PageModel can now declare IPetService in its constructor and get it injected.
// This is the .NET Core way — no more static helper classes or newing up data access objects.
builder.Services.AddScoped<IPetService, PetService>();
// Home merges medication schedules and vet appointments into each pet's care reminders.
builder.Services.AddScoped<IMedicationService, MedicationService>();
builder.Services.AddScoped<IVetVisitService, VetVisitService>();
builder.Services.AddScoped<IHealthService, HealthService>();
builder.Services.AddScoped<IHouseholdContextService, HouseholdContextService>();
builder.Services.AddScoped<IHouseholdAuthorizationService, HouseholdAuthorizationService>();
builder.Services.AddScoped<IHouseholdService, HouseholdService>();
builder.Services.AddScoped<IHouseholdInvitationService, HouseholdInvitationService>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<IUserTimeZoneService, BrowserTimeZoneService>();
// One instance for the app lifetime: storage holds paths/loggers, not per-user state.
// Home uses image storage for photos and document storage when deleting a pet's attachments.
builder.Services.AddSingleton<IPetImageStorage, PetImageStorage>();
builder.Services.AddSingleton<IVetVisitDocumentStorage, VetVisitDocumentStorage>();
// The host supplies IConfiguration, IWebHostEnvironment and ILogger<T> to constructors.

// --------------------
// Build the app
// --------------------
var app = builder.Build();

// --------------------
// Middleware Pipeline — ORDER MATTERS
// Each request passes through these in sequence like a chain
// In .NET Framework this was HttpModules and HttpHandlers in web.config
// --------------------

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles(); // Serves wwwroot files (CSS, JS, images)

var configuredUploadRoot = builder.Configuration["PetImages:UploadRoot"]
    ?? (app.Environment.IsDevelopment() ? "uploads" : "/var/www/petpotty/uploads");
var uploadRoot = Path.IsPathRooted(configuredUploadRoot)
    ? configuredUploadRoot
    : Path.GetFullPath(configuredUploadRoot, app.Environment.ContentRootPath);
Directory.CreateDirectory(uploadRoot);
// Uploaded pet photos are served by PetImageModel after an ownership check.
app.UseRouting();     // Figures out which page/route handles this request

// Make session available before page handlers; authenticate/authorize before endpoints run.
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();
