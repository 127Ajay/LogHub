# Phase 3 - Future

**Status: partially resolved (2026-07-21).** The log-types item turned out to need no code. The
other two remain deferred, with a written plan for picking them up later. The trigger conditions
below matter as much as the features themselves.

Shared context (objective, architecture, non-functional requirements) is in `00-overview.md`.

## Candidate scope

- **Extend to Windows Services, WCF, and Web API log types.** **Resolved 2026-07-21 - no code
  required.** There is no application-type concept in the codebase; `LogApplicationConfig` is a
  name plus folder paths. Any application writing flat `*.log` files is already supported by
  registering its folder. Verified against NLog, log4net, and bracketed ASP.NET layouts on a
  running instance - see `docs/plans/2026-07-21-phase-3-log-types.md`. Applications that log
  **only to the Windows Event Log** remain out of scope and would need a genuine second collection
  mechanism.
- **Multi-server support with a collector agent** - **deferred.** Only if logs move off the single
  server LogHub runs on. This was the original, more complex architecture considered in early PRD
  drafts and deliberately simplified away once it was confirmed everything lives on one server.
  Don't reintroduce agent/ingestion-API complexity unless that assumption has actually changed.
  Note that a UNC path already works with no code change, which should be ruled out first. Plan:
  `docs/plans/2026-07-21-phase-3-future-multiserver-and-auth.md`.
- **Lightweight authentication** - **deferred.** Only if the tool's audience grows beyond "internal
  network only" is comfortable for. The PRD's suggested starting point is Windows Integrated Auth
  (no extra licensing, uses the existing domain) rather than building a full identity/roles system
  from scratch. Plan:
  `docs/plans/2026-07-21-phase-3-future-multiserver-and-auth.md`.

## Trigger conditions (don't build ahead of these)

| Feature | Build it when... |
|---|---|
| Non-Web-Application log types | ~~Resolved~~ - flat-file applications of any type work today. Only **Windows Event Log** sources remain gated: build when a concrete application that logs solely to the Event Log needs monitoring |
| Multi-server / collector agent | Logs genuinely need to be read from a server other than the one running LogHub |
| Authentication | The tool's user base or network exposure has grown past what "internal network only" reasonably covers |

## Notes for whoever picks this up

- Re-read `00-overview.md`'s architecture decisions before starting - several of them
  (single-server, no auth) were explicit trade-offs for Phase 1's scale, not universal constraints.
  Phase 3 is where those get revisited, but only for the specific trigger that's actually occurred.
- If multi-server support is built, keep the "no database" decision under scrutiny - it may still
  hold (e.g. a lightweight agent forwarding to the existing file-based model) or it may need
  revisiting; don't assume either way without checking current constraints with the user first.
