# LogHub

A centralized, web-based log viewer for internal applications.

Internal web applications on a server write their own `*.log` files to disk. Reading them normally
means remoting into the server and hunting through folders. LogHub reads those files directly and
serves them in a browser — live tail plus searchable history — so developers and support staff
never need server access just to read a log.

**Single server. No database. No search engine. No login.** It reads `*.log` files off the disk of
the machine it runs on, and that is the whole architecture.

## Status

| Phase | State |
| --- | --- |
| [Phase 1 — pilot](docs/prd/phase-1-pilot.md) | Implemented |
| [Phase 2](docs/prd/phase-2.md) | Mostly implemented — regex/boolean search, date-range export, index caching, redaction. **Alerting deferred**, not designed. |
| [Phase 3](docs/prd/phase-3.md) | Partially resolved. Non-web-app log types need no code — any application writing flat `*.log` files works today. Multi-server and authentication are **deferred**, with a [written plan](docs/plans/2026-07-21-phase-3-future-multiserver-and-auth.md) for adding them later. |

## Features

- **Live tail** — polls each registered application's log file(s) for today and pushes new entries
  to the browser over SignalR. Filter the stream by file, level, and keyword.
- **History** — pick a date, filter by keyword or level, group by structured fields discovered in
  the logs themselves. Export to CSV.
- **Admin** — register and remove applications (a name plus one or more folder paths) from the
  browser. No config file editing, no restart.
- **Format-agnostic parsing** — no per-application format configuration. Timestamp and level
  detection are heuristic, so LogHub keeps working against log formats it has never seen.
- **Multi-line entries** — a line without a timestamp is folded into the previous entry, so stack
  traces stay attached to the message that produced them.
- **Redaction** — credential-looking values are masked before entries leave the server. A safety
  net, [not a guarantee](#a-note-on-redaction).

Supported on-disk layouts: flat (`Root/app-2026-07-20.log`), one folder per day
(`Root/2026-07-20/*.log`), and split nesting (`Root/2026/07/20/`). The scan is recursive, depth
capped at 6. A file's date falls back through name → folder path → last-write time, so a readable
file always lands somewhere in the index.

## Quick start

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd src/LogViewer.Web
dotnet restore
dotnet run
```

Open the URL from the console (typically `http://localhost:5000`). The app ships pre-seeded with a
`CustomerPortal` entry pointing at `sample-logs/`, so Live Tail and History have something to show
before you register anything real. Go to **Admin** to add your own.

Relative paths resolve against the process working directory — prefer absolute paths
(`C:\Logs\MyApp`) for real applications.

> The SignalR JavaScript client is vendored at `wwwroot/lib/signalr/`. Keep it checked in — there
> is no CDN fallback, by design, since the tool is meant to run on an isolated network.

For detailed setup, Serilog configuration, and IIS/Windows Service deployment notes, see
[`src/LogViewer.Web/README.md`](src/LogViewer.Web/README.md).

## How it works

```
Browser ──── SignalR (live) ────┐
        └─── HTTP  (history) ───┤
                                ▼
                        ASP.NET Core (.NET 8)
                                │
              LogFolderScanner  │  date -> files index, TTL cached
              LogLineParser     │  timestamp / level / tag detection
              LogEntryGrouper   │  multi-line entry assembly
              LogRedactor       │  masks credential-looking values
                                ▼
                       *.log files on local disk
```

`LogTailBackgroundService` polls today's files on an interval and pushes new entries to the
per-application SignalR group. History reads the relevant files on request — nothing about the log
content is persisted.

## Design decisions

These were settled during requirements work and should not be reversed without a reason. The full
log is in [`docs/prd/00-overview.md`](docs/prd/00-overview.md).

| Decision | Why |
| --- | --- |
| Single server | The app and the logs live on the same machine. No agents, no shipping. |
| No database or search index | Elasticsearch/SQL were explicitly ruled out. Files are read from disk per request. |
| No authentication | Internal network only. Windows Integrated Auth is the path if this changes. |
| Registration via UI, not config | `App_Data/applications.json` is written by the Admin page. An `appsettings.json` app list was tried and replaced. |
| Polling, not `FileSystemWatcher` | Tolerates locked and rotating files without missed events. |
| TTL index cache, not invalidation | New files appear within the TTL. Consistent with polling. |
| Heuristic parsing, no format config | LogHub must keep working against formats it has not seen. |

### A note on redaction

`LogRedactor` masks what it can recognize on the way out. It cannot catch a secret logged as a bare
unlabelled string. Its presence is not a reason to relax the expectation that applications should
not log secrets, and logs should not be described as safe to share because it exists.

## Repository layout

```
LogViewer/
  CLAUDE.md                 Guidance for AI agents working in this repo
  README.md                 This file
  docs/prd/                 Requirements, split by phase
  docs/plans/               Design documents for individual changes
  src/LogViewer.Web/        The application (ASP.NET Core, .NET 8)
    Program.cs              Minimal API + Razor Pages + SignalR + Serilog wiring
    Models/  Services/  Hubs/  Pages/  wwwroot/
    App_Data/               Registered applications
    sample-logs/            Demo data for the seeded entry
    README.md               Setup, configuration, and deployment detail
```

## Operational logging

LogHub's own diagnostics go through Serilog to `src/LogViewer.Web/Logs/loghub-*.log`, rolling daily
— separate from the application logs it monitors. Configured in `Program.cs`, not
`appsettings.json`.

## Not in scope

Multi-server collector agents, authentication, and alerting are deliberately out of scope for now.
See [`docs/prd/phase-3.md`](docs/prd/phase-3.md) for the conditions under which each would be
revisited, and [the deferred-work plan](docs/plans/2026-07-21-phase-3-future-multiserver-and-auth.md)
for how multi-server and auth would be added.

Applications that log **only to the Windows Event Log** are also unsupported — LogHub reads files.
Any application that writes flat `*.log` files is supported regardless of its type.
