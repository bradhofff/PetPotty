using Microsoft.AspNetCore.Http;

namespace PetPotty.Services;

// Converts between a browser's local wall-clock time and UTC using the
// offset the browser itself reports (JavaScript's Date.getTimezoneOffset():
// minutes to ADD to local time to reach UTC, positive west of UTC). Only new
// health/household/invitation/adherence timestamps use this — legacy
// Task/VetVisit/Medication timestamps stay local-only by deliberate choice.
public interface IUserTimeZoneService
{
    // Used by a PageModel when no browser offset was submitted with the request.
    int FallbackOffsetMinutes { get; }
    DateTime ToUtc(DateTime local, int utcOffsetMinutes);
    DateTime ToLocal(DateTime utc, int utcOffsetMinutes);

    // Reads the "tzOffset" cookie _Layout.cshtml sets from the browser on
    // every page load (falls back to FallbackOffsetMinutes if absent/invalid)
    // — the one place GET-rendered pages should get "the current offset" from,
    // so a plain page view displays UTC timestamps in the viewer's own
    // timezone without every PageModel re-implementing cookie parsing.
    int GetRequestOffsetMinutes(HttpContext context);
}
