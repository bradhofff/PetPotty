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

// REQUIRED for session to work — stores session data in memory.
// In production you'd swap this for Redis or SQL-backed sessions.
// Without this line, Session will silently fail.
builder.Services.AddDistributedMemoryCache();

// Session configuration — replaces System.Web.SessionState from .NET Framework
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(60);
    options.Cookie.HttpOnly = true;    // JS cannot access this cookie (XSS protection)
    options.Cookie.IsEssential = true; // Cookie consent laws — this one is always allowed
});

// Cookie authentication — modern replacement for FormsAuthentication from .NET Framework.
// When a page requires auth and user isn't logged in, they get sent to /Login automatically.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
    });

builder.Services.AddAuthorization();

// Register our data service — AddScoped means one instance per HTTP request.
// Any PageModel can now declare IPetService in its constructor and get it injected.
// This is the .NET Core way — no more static helper classes or newing up data access objects.
builder.Services.AddScoped<IPetService, PetService>();

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
app.UseRouting();     // Figures out which page/route handles this request

// Session must come BEFORE UseAuthentication so session is available during auth
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();