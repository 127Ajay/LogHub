# Phase 3 - Future

**Status: Future / not scoped in detail.** Nothing here should be started without re-confirming it's
still wanted - this is further out than Phase 2, and the trigger conditions below matter as much as
the features themselves.

Shared context (objective, architecture, non-functional requirements) is in `00-overview.md`.

## Candidate scope

- **Extend to Windows Services, WCF, and Web API log types.** Phase 1 deliberately scoped to Web
  Applications only. Before extending: check whether these log to flat files the same way Web
  Applications do (Phase 1's `LogFolderScanner`/`LogEntryGrouper` assumptions should mostly hold),
  or whether they need a different collection mechanism (e.g. Windows Event Log, which was
  explicitly ruled out of Phase 1 scope because nothing needed it yet).
- **Multi-server support with a collector agent** - only if logs move off the single server LogHub
  runs on. This was the original, more complex architecture considered in early PRD drafts and
  deliberately simplified away once it was confirmed everything lives on one server. Don't
  reintroduce agent/ingestion-API complexity unless that assumption has actually changed.
- **Lightweight authentication** - only if the tool's audience grows beyond "internal network only"
  is comfortable for. The PRD's suggested starting point is Windows Integrated Auth (no extra
  licensing, uses the existing domain) rather than building a full identity/roles system from
  scratch.

## Trigger conditions (don't build ahead of these)

| Feature | Build it when... |
|---|---|
| Non-Web-Application log types | A concrete Windows Service/WCF/API application needs monitoring and Web-Application-only scope is now a blocker |
| Multi-server / collector agent | Logs genuinely need to be read from a server other than the one running LogHub |
| Authentication | The tool's user base or network exposure has grown past what "internal network only" reasonably covers |

## Notes for whoever picks this up

- Re-read `00-overview.md`'s architecture decisions before starting - several of them
  (single-server, no auth) were explicit trade-offs for Phase 1's scale, not universal constraints.
  Phase 3 is where those get revisited, but only for the specific trigger that's actually occurred.
- If multi-server support is built, keep the "no database" decision under scrutiny - it may still
  hold (e.g. a lightweight agent forwarding to the existing file-based model) or it may need
  revisiting; don't assume either way without checking current constraints with the user first.
