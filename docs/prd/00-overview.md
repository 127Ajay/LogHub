# LogHub - PRD overview

Source: `Log_Viewer_WebApp_PRD_v1.1.docx` (Draft v1.1, updated after stakeholder Q&A). This file
holds the context that applies across every phase - objective, architecture decisions, and
non-functional requirements. Phase-specific scope lives in `phase-1-pilot.md`, `phase-2.md`, and
`phase-3.md`.

**Read `CLAUDE.md` at the repo root first** for how the current codebase actually implements this.

## Objective

Enable developers and support staff to view live and historical logs from deployed applications
through one browser-based tool, without RDP/server access, by reading the existing `*.log` files
directly from disk on the server where the tool is installed.

## Problem statement

- Engineers must RDP into the server to view log files - slow, and requires standing server access
  for many people.
- Logs live in multiple folders per application, some flat and some organized as one dated
  subfolder per day with several files inside - there's no single place to browse them.
- No live/tail view - investigating an in-progress issue means repeatedly reopening a file on the
  server.
- Finding a past issue means guessing which date folder and which file it's in.

## Goals & success metrics

| Goal | Success metric |
|---|---|
| Eliminate manual server logins for log viewing | Developers/support use the web app instead of RDP for day-to-day log checks |
| Near real-time visibility | New log lines appear in the live view within a few seconds of being written |
| Easy historical lookup by date | Any log entry from a given date can be found within a couple of clicks, regardless of folder structure |
| Zero added licensing cost | No paid database/search engine required to run the tool |

## Target users

| Persona | Needs |
|---|---|
| Developer | Tail live logs while testing a fix; search historical logs by date/keyword when investigating a bug |
| Support Engineer | Quickly check today's or a past date's logs for a reported issue without asking a developer to RDP in |

## Decisions from stakeholder review (binding across all phases)

These came out of explicit Q&A and should not be silently reversed without a new decision:

| Question | Decision |
|---|---|
| Pilot scope - which servers/apps? | Web Application log types only. Windows Service, WCF, and API log types deferred to Phase 3. |
| Deployment topology | Single server: log files and LogHub are on the same machine. No remote agents, no cross-server network calls. |
| Retention / compliance (SOC2, HIPAA, etc.) | No formal compliance mandate. No secrets/sensitive data should be exposed in the viewer. Retention is governed by the existing log files/folders on disk, not by the application. |
| Authentication / identity provider | Not required. Internal-only tool for developers and support staff, not exposed publicly. |
| Elasticsearch / OpenSearch / SQL | Ruled out. The application reads `*.log` files directly from disk on demand - no database, no search index. |
| Windows Event Log sources | Not needed. All target applications log to flat files, across multiple folders, some organized as one folder per date containing multiple files. The viewer supports scanning multiple configured folders and date-based subfolder layouts. |
| Application registration | **Superseded during implementation**: originally planned as `appsettings.json` editing, later changed to a frontend Admin page backed by a JSON file (`App_Data/applications.json`) at explicit user request. No config-file editing or restart needed to add an application. |

## Architecture (applies to all phases unless a phase explicitly changes it)

Because the web application and the log files live on the same server, the architecture is
deliberately simple: no collector agents, no network log shipping, no central ingestion service,
and no database.

- **Web Application** (ASP.NET Core, hosted on the same server): serves the UI and does all log
  reading/parsing itself.
- **Folder/File Scanner**: for each registered application, walks its configured root folder(s),
  recognizing both flat log files and date-named subfolders, building an in-memory
  date -> file(s) index. Rebuilt on demand, not persisted.
- **Tailer**: polls the active log file(s) for an application and streams new lines to the browser
  in real time for the live tail view. (Polling, not FileSystemWatcher - see `CLAUDE.md`.)
- **Live push**: SignalR (WebSockets) pushes new lines to any open browser tab watching that
  application - in-process, since everything is on one server.
- **Search/filter**: performed on demand by reading and scanning the relevant file(s) - no
  pre-built search index.

## Non-functional requirements

| Category | Requirement |
|---|---|
| Latency | New log lines visible in live view within a few seconds of being written to disk |
| Resource footprint | Must not meaningfully impact the performance of the applications already running on the shared server |
| Data handling | No log content is persisted outside the existing `*.log` files; the viewer only reads them |
| Access | Reachable only on the internal network; no public exposure, no login required for v1 |
| Reliability | If a log file is locked/being rotated, the viewer should retry rather than fail |
| Browser support | Latest Chrome, Edge, Firefox |

## Technology stack & licensing

Since Elasticsearch/OpenSearch and SQL-based storage are both ruled out, the stack is built on
free, open-source .NET components already covered by existing Windows Server/IIS licensing - no
additional software to license.

| Layer | Technology | License cost |
|---|---|---|
| Web application | ASP.NET Core (.NET 8), hosted on IIS | Free - already covered by existing OS/IIS |
| Real-time push | SignalR (built into ASP.NET Core) | Free |
| File watching/tailing | Custom polling logic | Free |
| UI | Razor Pages + vanilla JS | Free |
| Storage | None - reads `*.log` files directly | N/A |

If log volume ever grows large enough that on-demand file scanning becomes slow, the free,
self-hosted **Lucene.NET** library is the suggested fallback for local full-text indexing - flagged
as a Phase 2+ option, not built preemptively.

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| Scanning large log folders on every request is slow | Build and refresh a lightweight in-memory date/file index instead of re-walking the filesystem per request |
| Sensitive data appearing in logs, exposed via the viewer | No secrets should be logged by the source applications; optionally add simple pattern-based redaction as a safety net (Phase 2) |
| No authentication means anyone on the internal network can view logs | Restrict at the network/firewall level; revisit adding lightweight auth if the tool's audience grows (Phase 3) |
| Inconsistent folder conventions across applications (flat vs. dated) cause missed files | Auto-detect layout per root folder at scan time |
| Log formats vary across frameworks and can break naive line-based parsing (e.g. multi-line entries) | Parsing is heuristic and format-agnostic - see `CLAUDE.md` "How log parsing works" |
