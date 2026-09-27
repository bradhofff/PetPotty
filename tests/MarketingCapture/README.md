# Landing page captures

This standalone, loopback-only host renders the application's compiled Razor pages
with fictional Scout / Juniper / Alex data. It never connects to a database and
blocks non-GET requests. Production `Program.cs` and authentication are unchanged.
The main project excludes this folder from its build and published content.

Run from the repository root:

```powershell
dotnet run --project tests/MarketingCapture/MarketingCapture.csproj --no-launch-profile
```

Capture these actual app pages at a consistent 1280 × 720 viewport, at the top of
each page, after the layout and styles have loaded:

| URL on http://127.0.0.1:5079 | Output in wwwroot/images/landing |
| --- | --- |
| /Home | dashboard.jpg |
| /Health | health.jpg |
| /Medications | medications.jpg |
| /VetVisits | vet-visits.jpg |
| /Reports | reports.jpg |

The landing page is `/`. Screenshots are static UI captures with fictional sample
data, disclosed in the page caption. Recapture after meaningful app UI changes.
This host intentionally does not run form handlers or account pages; verify login
and signup against the normal application host.

The landing page uses native scrolling, a sticky crossfading screen on desktop,
and inline screenshots on mobile. Reduced-motion users and browsers without
JavaScript get the inline, fully visible version.
