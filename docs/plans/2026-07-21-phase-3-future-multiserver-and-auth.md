# Phase 3 deferred: multi-server support and authentication

Date: 2026-07-21
Status: **planned, not implemented.** Deliberately deferred. Nothing here should be built until
the trigger for that specific item has actually fired.

This document exists so that whoever picks these up later does not have to re-derive the
reasoning. It is a plan, not a commitment.

## Why these two are deferred

Phase 3 (`docs/prd/phase-3.md`) listed three candidates. The first - extending beyond Web
Applications to Windows Services, WCF, and Web APIs - turned out to need no code (see
`2026-07-21-phase-3-log-types.md`). The remaining two are deferred because **neither trigger has
fired**:

| Item | Builds when |
| --- | --- |
| Multi-server / collector agent | Logs genuinely need reading from a server other than the one running LogHub |
| Authentication | The user base or network exposure grows past what "internal network only" reasonably covers |

Both would reverse a decision that was made deliberately, not by omission
(`docs/prd/00-overview.md`). Reversing them is legitimate when the trigger fires; doing it early
buys complexity for no benefit.

---

# Part 1 - Multi-server support

## Key finding: the cheapest option needs no code

`LogFolderScanner.ResolveRoot` passes any rooted path straight through:

```csharp
private string ResolveRoot(string root) =>
    Path.IsPathRooted(root) ? root : Path.GetFullPath(root, _contentRoot);
```

A UNC path (`\\server2\logs$\BillingService`) is rooted, so it flows unchanged into
`Directory.Exists` and the recursive scan. Both work over SMB. **Registering a UNC path in the
Admin page is already multi-server support**, subject only to permissions.

This should be tried and ruled out before anything is built. It is not a hack - it moves the
problem to the operating system, which already solves it.

## Options, cheapest first

| | Approach | New components | Works when |
| --- | --- | --- | --- |
| **A** | UNC share, registered as a root path | none | SMB reachable, same/trusted domain, LAN latency |
| **B** | Scheduled file copy into a folder LogHub already reads | a sync job (robocopy/rsync) | SMB not continuously reachable; some staleness acceptable |
| **C** | Collector agent pushing to an ingestion API | agent + API + storage | No SMB, WAN links, DMZ/untrusted network, many servers |

### Option A - UNC share (try first)

Work: grant the LogHub app pool / service account read access to the share, register
`\\server2\logs$\AppName` in Admin, verify History and Live Tail.

Watch for:
- **Identity.** Under IIS, the app pool identity must be a domain account with read rights, not
  `ApplicationPoolIdentity`, which has no network identity.
- **Latency.** The recursive scan runs per index rebuild; over a slow link, raise
  `IndexCacheSeconds` well above the default 10.
- **Tail polling cost.** `LogTailBackgroundService` stats every file every `PollIntervalSeconds`
  (default 2). Over SMB that is real network traffic - consider raising the interval per
  deployment.
- **Failure mode.** An unreachable share currently logs a warning and yields no files. Verify that
  degradation is acceptable, and consider surfacing it in the UI rather than only in the log.

### Option B - staged copy

A scheduled `robocopy /MIR` (or equivalent) from each remote server into a local folder tree that
LogHub reads normally. Zero LogHub changes. Cost is duplicated disk and staleness equal to the
copy interval. Reasonable stopgap; poor fit for live tail, which becomes "live as of the last
copy."

### Option C - collector agent

Only if A and B are genuinely unavailable. This is the architecture the original PRD draft
proposed and that was deliberately simplified away; reintroducing it should be a considered
decision with a written reason.

**Agent responsibilities**
- Watch configured folders on its own host (same scan/heuristics as `LogFolderScanner`, ideally by
  sharing that code as a small library rather than reimplementing it).
- Track a per-file checkpoint: path, inode/creation time, byte offset. Persist locally so a
  restart does not resend or skip.
- Batch new lines and POST them to LogHub.
- Buffer to local disk when the server is unreachable, with a bounded cap and an explicit
  drop-oldest policy.

**Protocol**
- `POST /api/ingest` accepting `{ agentId, application, sourceFile, entries[] }`.
- Batch size and flush interval configurable; default to whichever comes first (e.g. 500 entries
  or 2s) so live tail stays near real time.
- Compression on by default - log text compresses extremely well.
- Idempotency: each batch carries `(agentId, sourceFile, startOffset)`. The server rejects or
  ignores a batch it has already accepted, making retries safe at-least-once without duplicates.

**Authentication between agent and server**
- Shared secret per agent as the minimum bar, sent as a header, compared in constant time.
- mTLS if the network is untrusted.
- Agents are write-only: the ingestion endpoint must never expose read APIs.

**Server side**
- The single-server model assumes files on local disk. Ingested entries have no file to re-read,
  so this is the point where **the "no database" decision must be re-examined** rather than
  assumed. Options, in order of preference:
  1. Agent writes into a per-server folder tree on the LogHub host; everything downstream is
     unchanged and the file-based model survives intact. **Strongly preferred** - it keeps one
     code path.
  2. A lightweight append-only store (SQLite, or date-partitioned files) if (1) does not fit.
  3. A real database, only with a written justification against `00-overview.md`.
- Retention becomes LogHub's problem once it holds copies. Define a policy before shipping.

**Operational**
- Version skew: agents will not upgrade in lockstep. Version the ingestion contract from day one.
- Clock skew: agents timestamp on their own host. Either trust the parsed log timestamp (preferred,
  consistent with the existing wall-clock rule) or record both and be explicit about which is shown.
- Observability: per-agent last-seen, backlog depth, and drop count, surfaced in the UI. A silently
  dead agent looks exactly like a quiet application.
- Deployment: the agent is now software to install, patch, and monitor on every server. That
  ongoing cost is the main reason to prefer A or B.

## Recommended sequence

1. Confirm the trigger: which server, which application, why local is not possible.
2. Try Option A. Measure scan and poll cost over the real link.
3. If A fails, ask whether staleness is acceptable; if yes, Option B.
4. Only then Option C, and only with sub-option (1) for storage unless proven unworkable.

## Risks

- Building C when A would have worked - the most likely and most expensive mistake here.
- Silent agent failure presenting as "the application is quiet."
- Retention and disk growth on the LogHub host once it stores copies.
- The `no database` decision eroding by default rather than by decision.

---

# Part 2 - Lightweight authentication

## Trigger

Not "it would be nice." Specifically: the audience or network exposure has grown past what
internal-network-only covers - for example the tool becomes reachable from a wider VLAN or VPN, or
non-technical staff who should not see all applications need access.

## Recommended: Windows Integrated Authentication

The PRD's suggested starting point, and still the right one: it uses the existing domain, needs no
extra licensing, no password storage, and no identity system to build or operate.

**Wiring**
- Add `Microsoft.AspNetCore.Authentication.Negotiate`.
- Register Negotiate authentication and add `UseAuthentication()` / `UseAuthorization()` in
  `Program.cs`.
- Under IIS, enable Windows Authentication and disable Anonymous for the site.
- Kestrel supports Negotiate directly if not hosted behind IIS.

**What is affected - do not miss these**
- **Every minimal API endpoint** under `/api/*`. They are currently open; each needs a policy.
- **The SignalR hub.** `/hubs/log` needs `[Authorize]`, and the JS client must send credentials.
  This is the piece most likely to be forgotten and to fail confusingly at runtime.
- **The Admin page**, which can register arbitrary filesystem paths. Even in a fully trusted
  deployment this deserves a tighter policy than read-only pages.

## Staged authorization model

Do not build all three at once. Each stage is independently useful.

| Stage | Rule | Cost |
| --- | --- | --- |
| 1 | Any authenticated domain user may read; everyone sees everything | Small. Mostly wiring. |
| 2 | Admin page and mutating `/api/apps` endpoints restricted to an AD group | Small. One policy, group name in config. |
| 3 | Per-application access: each registered app names the AD groups allowed to view it | Real work - see below. |

Stage 3 touches `LogApplicationConfig` (an `AllowedGroups` list), the Admin UI, the app list
endpoint, History, export, **and the SignalR group model** - a user must not be able to join
`app:<name>` for an application they cannot see. Treat it as its own piece of work with its own
design, not as an extension of stages 1-2.

## Alternatives

- **Reverse proxy authentication** (IIS ARR, nginx, or an existing SSO gateway). Attractive when
  one already exists: LogHub stays unauthenticated and trusts a header. Only acceptable if the app
  cannot be reached except through the proxy - otherwise the header is trivially forged.
- **Entra ID / OIDC.** The right answer if the tool is ever exposed beyond the corporate network,
  or if access must be audited centrally. Heavier: app registration, redirect handling, token
  lifetimes, and a dependency on reachable identity infrastructure.

## Non-goals

Local accounts, a password store, self-service registration, or a bespoke roles system. If the
requirement grows to need those, that is a signal to adopt Entra ID rather than to build them.

## Risks and gotchas

- **Redaction is not access control.** `LogRedactor` masks credential-shaped values on the way out.
  It does not make logs safe for a wider audience, and adding auth must not be justified by its
  presence, nor its absence excused by auth.
- **Service accounts.** If LogHub reads UNC shares (Part 1, Option A) it needs a domain identity of
  its own. That identity is separate from the user's, and both need to be right.
- **Auditing.** Once there is a notion of who someone is, "who read which logs" becomes an
  answerable and probably expected question. Decide whether to log it before someone asks.
- **Local development.** Negotiate against a domain is awkward off-network. Keep a documented way
  to run locally without auth, and make sure it cannot be enabled accidentally in production.

---

# If both are needed

Do authentication first. Multi-server widens what the tool can reach; doing that before there is
any notion of who is asking increases exposure at exactly the moment the blast radius grows. If
Option C (agent) is in play, its agent-to-server credential is a separate concern from user
authentication - do not conflate them.

# Open questions to settle before starting either

1. Which specific server and application motivates multi-server, and why can the logs not be read
   locally?
2. Is SMB reachable between the hosts? Under which identity?
3. What staleness is acceptable for a non-local log - seconds, or minutes?
4. Who is the widened audience for authentication, and should they see all applications or a subset?
5. Is there an existing SSO gateway or standard the tool should adopt rather than choosing its own?
6. Does anyone need to answer "who viewed these logs" after the fact?
