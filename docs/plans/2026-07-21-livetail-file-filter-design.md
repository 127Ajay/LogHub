# Live Tail: per-file selection

Date: 2026-07-21
Status: designed, not implemented

## Problem

Live Tail streams every one of today's log files for the selected application into a single
pane. When an application's folder holds more than one file - `app.log` and `errors.log`, say -
the lines interleave and there is no way to look at just one of them. The Application dropdown
is the only selector on the page.

## Decision

Filter by file **on the client**. No server-side change of any kind: no C#, no new endpoint, no
change to `LogHub`, `LogTailBackgroundService`, or the SignalR group scheme.

This is possible because everything needed already exists:

| Need | Already provided by |
| --- | --- |
| Which files exist for today | `GET /api/apps/{name}/files?date=<today>` (`Program.cs:96`) |
| Which file a streamed entry came from | `entry.sourceFile` (set in `LogTailBackgroundService.cs:97`, `LogHub.cs:63`; already rendered in the `.src` span) |

Both sides use `Path.GetFileName`, so the endpoint's values and `entry.sourceFile` are directly
comparable - no path normalization required.

### Alternatives rejected

- **Server-side per-file SignalR groups** (`app:<name>|file:<name>`). Less traffic on the wire,
  but it rewrites group semantics, `JoinApp`/`LeaveApp`, and backlog replay, to solve a problem
  that is not traffic. Rejected as disproportionate.
- **Single-file dropdown.** Simpler markup, but you cannot watch two of three files at once,
  which is the common case when correlating an app log against an error log.

## Files touched

- `wwwroot/js/livetail.js` - the substance of the change
- `Pages/LiveTail.cshtml` - a "Log file" block in the existing `.filterpanel`
- `wwwroot/css/site.css` - only if the file list needs a scroll constraint

## Structure

`appendLine` currently filters, builds DOM, caps length, and scrolls in one function. Splitting
it is what makes re-rendering possible:

```
passesFilters(entry) -> bool     // level + keyword + file
renderOne(entry)                 // build <div class="logline">, append
render()                         // clear pane, replay buffer through passesFilters
appendLine(entry)                // hub callback: ensureFileToggle -> buffer -> maybe renderOne
```

State added: `buffer` (array, capped at the existing `MAX_LINES` of 500), `knownFiles` (object
used as a set), `fileToggles` (array, mirroring the existing `levelToggles`).

The DOM cap moves to the buffer. Today the code trims `stream.children` after every append; once
the buffer is the source of truth the DOM is just a projection of it, and the
`while (stream.children.length > MAX_LINES)` loop goes away.

### Filtering is retroactive

`fileToggles`, `levelToggles`, and `keywordFilter` all call `render()` on change. Ticking a
single file immediately shows only that file's buffered history rather than only affecting lines
that arrive later.

This also fixes the level toggles and the keyword filter, which are forward-only today - unticking
"Debug" currently leaves existing debug lines on screen. Autoscroll is re-applied at the end of
`render()` so the pane does not jump to the top after re-filtering.

### Pause keeps discarding

`appendLine` keeps its `if (paused) return;` guard ahead of the buffer push, so lines arriving
while paused are dropped and never appear on resume. Buffering-while-paused was considered and
explicitly declined: it is a behaviour change beyond the scope of this request.

## Behaviour

On app select:

1. `LeaveApp(prev)` / `JoinApp(next)` as today.
2. Reset `buffer`, `knownFiles`, `fileToggles`, and the pane.
3. Fetch `/api/apps/<app>/files?date=<today>`, build a checked checkbox per file.

The date is computed browser-side from `new Date()`. This matches how `formatTimestamp` already
treats timestamps as local wall-clock time, consistent with the "timestamps keep their wall-clock
value" rule in CLAUDE.md.

Per entry:

1. `ensureFileToggle(entry.sourceFile)` - an unseen name appends a new toggle, already checked.
2. Push to the ring buffer.
3. Render if it passes all filters.

## Edge cases

**An empty file list must fail open.** If the fetch fails, or the application has no files for
today, `fileToggles` is empty and a naive `activeFiles().indexOf(name) === -1` test hides every
line - the pane goes blank and reads as broken. When no toggles exist the file filter is a no-op.
A failed or 404 fetch logs to the console and shows everything.

**Files appearing mid-session are auto-added, checked.** A new file created during the session,
or a rotation producing a new name, arrives with a `sourceFile` that is not in the list.
`ensureFileToggle` appends it already ticked, so nothing is ever hidden by accident and the list
heals itself without polling. This follows the codebase's standing principle that a line is never
rejected for failing to match a known shape (CLAUDE.md, key decision 8).

**Join races the fetch.** `JoinApp` replays backlog immediately, so entries can arrive before the
files fetch resolves. `ensureFileToggle` makes this safe - whichever path arrives first creates
the toggle and the other is a no-op.

## Known limitation (pre-existing, not addressed)

`sourceFile` is a bare filename. An application registered with two folders that each contain an
`app.log` collapses into one checkbox governing both, and the two files' entries are
indistinguishable. This ambiguity already exists in the current UI - the `.src` column shows the
same bare name for both - and this change neither introduces nor worsens it. Resolving it means
carrying a relative path in `sourceFile`, which touches the tailer, the hub, History, and CSV
export. Out of scope.

## Verification

There is no test project in this repository, so verification is manual:

1. Create `src/LogViewer.Web/sample-logs/CustomerPortal/2026-07-21/` containing `app.log` and
   `errors.log`. The sample data currently stops at 2026-07-20, so Live Tail has nothing to tail
   today. Note this new folder is **not** covered by `.gitignore`, which names the 07-19 and
   07-20 files explicitly.
2. `dotnet build`, then `dotnet run`, and open Live Tail.
3. Append lines to each file and confirm:
   - both files appear in the list as checked toggles;
   - unticking one immediately clears its already-rendered lines;
   - a third file created mid-session appears in the list, checked, with its entries shown;
   - unticking everything empties the pane, and re-ticking restores from the buffer;
   - Pause still discards - lines written while paused do not appear on resume;
   - level and keyword filters now also re-filter retroactively.
