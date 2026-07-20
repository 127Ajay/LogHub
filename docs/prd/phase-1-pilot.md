# Phase 1 - Pilot

**Status: Implemented.** This describes what Phase 1 was scoped to build and what actually shipped,
including where implementation diverged from the original plan. See `CLAUDE.md` for the current
code layout and internals.

Shared context (objective, architecture, non-functional requirements) is in `00-overview.md` - this
file only covers what's in and out of scope for this phase.

## Scope

### In scope

- Live tailing and historical browsing of `*.log` files written by Web Applications on the same
  server as LogHub.
- Multiple configured root folders per application (an app's logs are not assumed to live in one
  single folder).
- Date-organized folder layouts (e.g. `Logs\2026-07-20\*.log`) as well as flat rolling-file layouts
  (e.g. `Logs\app-2026-07-20.log`), auto-detected per root folder.
- A date picker so a user can jump straight to "what happened on July 15" without knowing the
  folder layout.
- Keyword and log-level based search/filter within a selected date/file(s).
- No authentication - reachable only on the internal network.

### Out of scope for this phase

- Windows Services, WCF, and Web API log types (Phase 3).
- Multi-server / remote agent collection (Phase 3, only if ever needed).
- Any persistence of log content into a database, search index, or cache store.
- Login/authentication, roles, audit trail (Phase 3, only if audience grows).
- Alerting/notifications (Phase 2).

## What actually shipped (including divergences from the original plan)

- **Live Tail** - polls each app's today's file(s) every ~2 seconds, streams new entries to the
  browser via SignalR. Color-coded by level, in-stream keyword filter, pause/resume, auto-scroll.
- **History** - date picker (populated from the actual date/file index, not guessed), file
  selector, keyword + level filters, CSV export.
- **Application registration diverged from the original plan.** The PRD originally assumed
  `appsettings.json` editing (see `00-overview.md`'s decision log). During implementation, a user
  explicitly requested the ability to add applications from the frontend instead. This is what
  shipped:
  - An **Admin page** (name + one or more log folder paths, add/remove).
  - Backed by `App_Data/applications.json`, a plain file the app reads on startup and rewrites on
    every change - still "no database" per the architecture decision, just UI-managed instead of
    hand-edited.
  - No restart needed to register or remove an application.
- **Log parsing is more robust than originally specced**, as a direct result of a real bug found
  after adding Serilog logging to LogHub itself and pointing LogHub at its own log file. Specifically:
  - Level detection recognizes both full words (`ERROR`/`WARN`/`INFO`/`DEBUG`) and Serilog's
    bracketed 3-letter codes (`[ERR]`/`[WRN]`/`[INF]`/`[DBG]`/`[VRB]`/`[FTL]`).
  - Multi-line log entries (stack traces, wrapped messages) are grouped under the timestamp that
    started them, instead of being torn into disconnected, unparseable lines. This was deliberately
    generalized - not hardcoded to Serilog's output shape - because the requirement is "work with
    any log files," not "work with Serilog." See `CLAUDE.md` for exactly how this works.
- **LogHub's own operational logging uses Serilog**, writing to a rolling daily file under `Logs/`
  (plus console) - separate from the `*.log` files it monitors. Added on request; not in the
  original PRD, but doesn't change the "no database" decision (still just files).
- A JSON enum-deserialization bug in the original `LogApplicationConfig.Layout` field caused
  registered applications to silently disappear on every restart. The `Layout` field was unused by
  the scanner (it always auto-detects layout regardless), so it was removed entirely rather than
  patched - one less footgun, no functionality lost.

## Acceptance criteria

- A developer can register a Web Application's log folder(s) from the Admin page without editing
  any file or restarting the app.
- Live Tail shows new lines within a few seconds of them being written, correctly leveled and
  readable even for multi-line entries.
- History can find and display log entries for any past date the application has data for, whether
  that date's logs live in a dated subfolder or a flat file named for that date.
- Keyword search matches against the full (possibly multi-line) message, not just the line that
  happened to contain the timestamp.
- No database, no login screen, no dependency on anything outside the local filesystem.
