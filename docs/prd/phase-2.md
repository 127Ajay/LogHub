# Phase 2

**Status: Mostly implemented (2026-07-20).** Four of the five candidate items are built; alerting
was explicitly deferred by the user. See per-item status below.

Shared context (objective, architecture, non-functional requirements) is in `00-overview.md`.

## Candidate scope

- **Search/filter refinements** - *Done.* `Services/LogQuery.cs`. Keyword mode supports implicit
  AND, explicit `OR`, `NOT`/`-term` exclusion, and `"exact phrases"`; an optional regex mode treats
  the whole box as a .NET regular expression (2-second match timeout, invalid patterns return a 400
  with the parser's message rather than an empty table). Matching runs against the **grouped**
  entry, so a term inside a stack trace still finds the entry that owns it.
- **Export improvements** - *Done.* `GET /api/apps/{name}/export?from=&to=` streams CSV across a
  date range with the same level/keyword/tag filters as the on-screen search, sharing the
  `ReadEntries` helper so the two can't drift. Capped by `MaxExportEntries` (default 100,000).
- **In-memory indexing performance tuning** - *Done, in its simplest useful form.* `LogFolderScanner`
  caches the date -> files index in memory for `IndexCacheSeconds` (default 10), invalidated when an
  app is registered or removed. Measured on a 4,032-file tree: ~35 ms of scan removed per request.
  This is a **TTL cache, not the "approximate offset" index** this doc originally sketched - that
  wasn't needed to remove the observed cost, and the simpler thing was preferred. No database, no
  search engine. If this ever proves insufficient the fallback remains **Lucene.NET** (free,
  self-hosted), not Elasticsearch/SQL.
- **Redaction safeguard** - *Done.* `Services/LogRedactor.cs`, on by default (`RedactSecrets`).
  Masks `password=`/`token=`/`api_key=`-style assignments, `Bearer`/`Basic` credentials, and bare
  JWTs, in messages and in tag values; tag keys that name secrets are excluded from the group-by
  dropdown entirely. Applied **after grouping**, so secrets inside stack-trace continuation lines
  are covered. Log files on disk are never modified. Treat this as a safety net only - it cannot
  catch a secret logged as a bare string with no surrounding context.
- **Alerting/notifications** - *Not started; deliberately deferred* (confirmed with the user
  2026-07-20). Still has no design work: delivery mechanism, rule storage, and whether it needs any
  persistence at all are all open, and the last of those bears directly on the no-database decision.
  Scope it properly before starting.

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
  `LogQuery.Matches` takes a whole `LogEntry` for exactly this reason; don't add an overload that
  takes a raw line.
- Redaction ordering in `LogRedactor` is load-bearing: scheme-prefixed tokens (`Bearer x`) are
  masked *before* the general `key=value` rule. With the order reversed, `Authorization: Bearer <token>`
  matches the general rule with value `Bearer`, masking the scheme word and leaving the real token
  visible. There is a negative lookahead guarding the same case - don't remove either.
