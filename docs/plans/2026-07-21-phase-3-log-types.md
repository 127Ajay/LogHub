# Phase 3: extending beyond Web Applications

Date: 2026-07-21
Status: **resolved - no code required.**

## The item

Phase 3's first candidate was "extend to Windows Services, WCF, and Web API log types." Phase 1
deliberately scoped to Web Applications only, and the open question was whether the other types
would need a different collection mechanism (the Windows Event Log was explicitly ruled out of
Phase 1).

## Finding

**There is no application-type concept anywhere in the codebase.** `LogApplicationConfig` is:

```csharp
public class LogApplicationConfig
{
    public string Name { get; set; } = "";
    public List<string> RootPaths { get; set; } = new();
}
```

A name and folder paths. Nothing in `LogAppRegistry`, `LogFolderScanner`, `LogLineParser`,
`LogEntryGrouper`, or `LogTailBackgroundService` branches on what kind of application produced a
file. "Web Applications only" was a scoping decision in the PRD, never a constraint in the code.

Any application that writes flat `*.log` files to a folder - regardless of whether it is a web
app, a Windows Service, a WCF service, or a Web API - is already supported. Register the folder in
Admin and it tails and searches like anything else.

## Verification

Confirmed against a running instance (not by inspection alone) using log output in the layouts
these application types actually emit, registered through `POST /api/apps` and read back through
`/api/apps/{name}/logs`:

| Format | Sample shape | Timestamp | Level | Multi-line |
| --- | --- | --- | --- | --- |
| NLog default (Windows Service) | `2026-07-21 10:00:00.1234\|INFO\|Billing.Worker\|msg` | parsed | Info / Warn / Error | stack trace folded into one 4-line entry |
| log4net (WCF) | `2026-07-21 10:05:00,123 [12] INFO Acme.Wcf - msg` | parsed, comma milliseconds handled | Info / Error | n/a |
| ASP.NET Web API | `[2026-07-21 10:09:00] [Information] traceId=... status=200` | parsed, bracketed form handled | `Information` mapped to Info | n/a |

All three files in one folder were indexed together, dates resolved correctly, and the NLog
exception with its two `at ...` frames stayed attached to its parent entry rather than splitting
into separate rows.

The probe application and its sample files were removed after the check; nothing was left
registered.

## What remains genuinely out of scope

Applications that do **not** write flat files - specifically those logging only to the **Windows
Event Log**. That would need a second collection mechanism: a source type on
`LogApplicationConfig`, an Event Log reader, a mapping from `EventLogEntry` to `LogEntry`, Admin UI
to pick a log and source, and a polling path for live tail. It was ruled out of Phase 1 because
nothing needed it, and it remains unbuilt because nothing needs it yet.

Build it when a concrete application that logs only to the Event Log needs monitoring - not before.

## Consequence for the docs

The "Web Applications only" wording in the PRD describes the pilot's scope, not a limitation of
the tool. Any application writing flat log files can be registered today.
