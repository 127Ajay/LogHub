# LogHub — Phase 1 (pilot)

Centralized log viewer for internal Web Applications, built from `Log_Viewer_WebApp_PRD_v1.1.docx`.
No database, no auth, no remote agent — it reads `*.log` files directly from disk on the same
server it runs on.

## What's implemented (Phase 1 scope)

- **Live tail** — polls each registered application's log file(s) for today every few seconds and
  pushes new lines to the browser over SignalR.
- **History** — date picker + keyword/level filter over any past date, reading the relevant file(s)
  directly. Handles both flat files (`Root\app-2026-07-20.log`) and one-folder-per-day layouts
  (`Root\2026-07-20\*.log`), including multiple files inside a single date folder.
- **CSV export** of whatever's currently on screen in History.
- **No login** — intended for the internal network only, per the PRD.
- **Admin page** to register/remove applications from the browser — no config file editing or
  restart required.
- **LogHub's own diagnostics run through Serilog**, written to a rolling daily file under `Logs/`
  (plus the console) — separate from the `*.log` files it's monitoring. Useful if the tool itself
  has trouble (a bad path, a locked file, a startup error) and you need to know why.

Registered applications are stored in `App_Data/applications.json`, a plain file the app reads on
startup and rewrites whenever you add or remove an application from the Admin page. This is still
"no database" per the PRD — it's just a flat file instead of a config file you'd hand-edit.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A browser (Chrome/Edge/Firefox)

## One-time setup: SignalR client library

The live tail page needs the SignalR JavaScript client at `wwwroot/lib/signalr/signalr.min.js`.
Rather than pointing the page at a public CDN (which may not be reachable from an internal server),
fetch it once during development and it'll ship with your build output:

```bash
cd LogViewer.Web
dotnet tool install -g Microsoft.dotnet-libman   # one-time, if you don't have libman
libman install @microsoft/signalr@8.0.7 -p unpkg -d wwwroot/lib/signalr --files dist/browser/signalr.min.js
```

If `libman` isn't available in your environment, you can instead run `npm install @microsoft/signalr`
anywhere with internet access and copy `node_modules/@microsoft/signalr/dist/browser/signalr.min.js`
into `wwwroot/lib/signalr/`.

## Run it

```bash
cd LogViewer.Web
dotnet restore
dotnet run
```

Then open the URL shown in the console (typically `https://localhost:5001` or `http://localhost:5000`)
and go to **Admin** to register your first application: a name plus one or more log folder paths.
You can add more than one path per application if its logs are split across locations, and add or
remove applications at any time — changes take effect immediately, no restart needed.

The project ships with `App_Data/applications.json` pre-seeded with one sample entry
("CustomerPortal") pointing at the `sample-logs/CustomerPortal` folder included in this repo, so you
can see Live Tail and History working before registering anything real. Delete that entry from the
Admin page once you've registered your actual applications, or just leave it — it's harmless.

Note that relative paths (like the sample entry uses) resolve relative to the working directory
`dotnet run`/the deployed process is started from; for real applications, prefer absolute paths
(e.g. `C:\Logs\CustomerPortal`) to avoid ambiguity.

## LogHub's own logs

Serilog is wired up in `Program.cs` (not `appsettings.json`) to keep the setup in one place:

- Writes to `Logs/loghub-YYYYMMDD.log` (relative to the app's content root), rolling daily, keeping
  the last 14 files.
- Also writes to the console, so `dotnet run` output still shows what's happening.
- Default level is Information, with `Microsoft.AspNetCore` framework noise turned down to Warning.
- `UseSerilogRequestLogging()` logs one line per HTTP request (path, status, timing).

To change the minimum level or retention, edit the `UseSerilog(...)` call near the top of
`Program.cs` directly rather than adding a `Serilog` section to `appsettings.json` — keeping
config-driven levels was more machinery than a single-server pilot needs. If that changes later,
`Serilog.Settings.Configuration` can be added to read levels from `appsettings.json` instead.

## Deploying alongside the Web Applications it monitors

Since this is a single-server design, the simplest path is another site/app pool under the same IIS
instance already hosting the Web Applications, or run as a Windows Service via
`dotnet publish` + a service wrapper (e.g. `sc.exe create` pointing at the published exe, or the
`Microsoft.Extensions.Hosting.WindowsServices` package if you want `dotnet run` itself to register
as a service). Either way, no separate server or network path is required — this reinforces the
"no collector agent needed" decision in PRD v1.1 Section 8.

## What's deliberately not in Phase 1

Per the PRD: Windows Service / WCF / API log types, multi-server collection, any database or search
index, authentication, and alerting. See `Log_Viewer_WebApp_PRD_v1.1.docx` Section 15 for the
planned phases beyond this pilot.

## Project layout

```
LogViewer.Web/
  Program.cs                  Minimal API + Razor Pages + SignalR wiring
  Models/                     Config + log entry types
  Services/
    LogFolderScanner.cs       Builds the date -> files index (flat + dated-folder aware)
    LogLineParser.cs          Best-effort timestamp/level detection per line
    LogAppRegistry.cs         Reads/writes registered apps (App_Data/applications.json)
    LogTailBackgroundService.cs  Polls today's file(s), pushes new lines via SignalR
  Hubs/LogHub.cs               SignalR hub (per-app groups)
  Pages/                       Dashboard, Live tail, History, Admin (Razor Pages)
  wwwroot/                     site.css, livetail.js, history.js, admin.js
  App_Data/applications.json   Registered applications (created/edited via the Admin page)
  Logs/loghub-*.log            LogHub's own Serilog output (created at runtime)
```
