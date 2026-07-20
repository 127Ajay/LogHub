# Phase 2

**Status: Planned, not started.** Nothing in this file exists in the codebase yet. Treat it as
forward-looking guidance for after Phase 1 has been used in practice for a while, not an active
backlog. Confirm with the user before starting any of this - priorities may shift once Phase 1 gets
real usage.

Shared context (objective, architecture, non-functional requirements) is in `00-overview.md`.

## Candidate scope

- **Search/filter refinements** - more precise keyword matching (e.g. optional regex mode),
  multi-keyword/AND-OR queries, filtering by source file within a date more fluidly.
- **Export improvements** - beyond the current CSV export of on-screen results: exporting a full
  filtered date range without needing to page through the UI first.
- **In-memory indexing performance tuning** - Phase 1 scans files on demand for every History
  request. If usage/log volume makes this noticeably slow, build a lightweight in-memory
  date -> file -> approximate-offset index that's refreshed on file change, not persisted. This is
  explicitly **not** a database or search engine (see `00-overview.md`'s architecture decision) -
  if in-memory indexing turns out to be insufficient, the fallback is **Lucene.NET** (free,
  self-hosted, no license fees), not Elasticsearch/SQL.
- **Redaction safeguard** - simple pattern-based masking (e.g. anything that looks like a password
  or token) as a safety net, on top of the existing expectation that source applications shouldn't
  log secrets in the first place.
- **Alerting/notifications** - configurable rules (e.g. error count > N in 5 minutes, a specific
  keyword appears) triggering email/Teams/Slack notifications. This was noted as a stretch goal in
  the original PRD and has no design work done yet - scope it properly before starting (delivery
  mechanism, rule storage, whether it needs any persistence at all).

## Explicitly not this phase

- Extending to non-Web-Application log types (Windows Service, WCF, API) - that's Phase 3.
- Multi-server support - Phase 3, and only if the single-server assumption actually breaks down in
  practice.
- Authentication - Phase 3, and only if the tool's audience grows beyond what "internal network
  only" comfortably covers.

## Notes for whoever picks this up

- Don't add a database or search engine to satisfy performance needs before confirming that
  in-memory indexing genuinely isn't enough - re-read the "no SQL/Elasticsearch" decision in
  `00-overview.md` first; it was an explicit, considered choice, not a placeholder.
- Any new filter/search capability should keep working through `LogEntryGrouper` (multi-line-aware
  entries), not regress to raw per-line matching - see `CLAUDE.md`'s "How log parsing works."
