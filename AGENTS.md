# Agent rules — lancache-manager

Read this before editing anything in this repo. It is self-contained on purpose: state these rules,
don't go looking for the files behind them.

**This file is a backstop, not the contract.** Whoever launched you should also have stated the rules
that bind your specific task directly in your prompt. If your prompt and this file disagree, your prompt
wins for task specifics; the standing rules below never get overridden.

## Where you are

- Repo root: `H:\_git\lancache-manager` on Windows.
- **If you are running in WSL (cursor-agent does), use `/mnt/...` form for every path including `cwd`**:
  `/mnt/h/_git/lancache-manager`. A `H:\` or `C:\` path will not resolve and you will silently edit
  nothing, or the wrong tree. Rule: `X:\a\b` → `/mnt/x/a/b`.
- Backend: `Api/LancacheManager` (C#/.NET). Frontend: `Web/src` (React + TypeScript + Vite).
  Tests: `Tests/LancacheManager.Tests`.

## Do not explore

Do not run `git status`/`git diff`, `find`, `ls -R`, or repo-wide greps to work out your own scope, and
do not scan directories looking for context. This tree lives on a Windows drive mounted into WSL over
drvfs; broad scans are slow enough to time your session out before you produce anything. Read the exact
files your task names. If you genuinely cannot proceed without a file nobody named, say so and stop.

## Tools — one job each, and two of them pair

Applies only if you have these MCP tools; skip if you don't.

- **Serena = symbol engine.** `find_symbol(include_body:true)` reads one symbol instead of a whole
  file, `get_symbols_overview` gives a file's symbol table, `find_referencing_symbols` gives real
  compiler-resolved call sites, `replace_symbol_body`/`rename_symbol` edit surgically. It is NOT a
  note store: never `write_memory`, never `execute_shell_command` or `read_file`.
- **context-mode = sandbox + search cache.** Run bulk commands and greps through it so raw output
  stays out of the context window.
- **Pair them: breadth then depth.** Grep wide in the sandbox to find candidates, then confirm each
  with Serena. Grep overcounts (text matches, misses re-exports); Serena alone undercounts (you must
  already know the name). **Never report a call count from grep.**
- **Recall past decisions with search, not grep**: `ctx_search(queries: [4-6 differently-phrased
  queries], source: "brain")` in one call. Empty results mean the index needs rebuilding
  (`node ~/.claude/scripts/reindex-brain.mjs --if-stale 3`), not that nothing was recorded.
- **If you spawn subagents, they deliver via FILES.** Give each one a results-file path, require a
  `STATUS: RUNNING` header before its first step and `STATUS: COMPLETE` at the end, and read that
  file. An empty reply with a `COMPLETE` file means it succeeded — do not relaunch it.

## Reuse before you write

Before adding any function, component, hook, type, or constant, search for one that already exists,
and search for the **capability**, not just the name in your head — `FormatBytes` will not turn up in
a search for `humanizeFileSize`. Try name synonyms, the parameter/return type, and the call site that
would consume the result. When a change makes old code redundant, delete it rather than leaving it
beside the new pattern.

## Naming

- **Names do not accumulate.** Never rename an existing file, class, method, function, or variable as a
  side effect of editing it, and never bolt qualifiers onto a name because the change widened what it
  handles. `NormalizeClientRules` stays `NormalizeClientRules` — it does not become
  `NormalizeClientRulesForMultipleRules`. Swapping a working name for a synonym is equally out.
- **A rename may only ever REMOVE ceremony, never ADD words.** Stripping a banned word off a name you
  are already editing is welcome; lengthening a name is a defect.
- **Never introduce these words in new symbols**: `Dto`, `Info`, `Data`, `Payload`, `Metadata`,
  `Factory`, `Provider`, `Registry`, `Builder`, `Facade`, `Strategy`, `Adapter`, `Wrapper`,
  `Orchestrator`, `Coordinator`. Name things for what they ARE or DO (`EpicGameMappingDto` →
  `EpicGameMapping`, `CachedAppInfo` → `CachedApp`). The list is a floor — any equally abstract
  buzzword is out too.
- **These are fine and must not be "fixed"**: `Response`, `Request`, `Result`, `Service`, `State`,
  `Options`, `Config`, `Settings`, `Snapshot`, `Extensions`, `Base`, `Helper`, `Utils`, `Manager`,
  `Core`.
- No hedging in identifiers — booleans state facts (`scanStale`, not `scanMayBeStale`).

## Never write AI provenance into the code

No "Claude", "Cursor", "codex", "GPT", "AI", "swarm", "worker N", severity words (CRITICAL/HIGH/MED/LOW),
or process words ("review", "finding", "per plan") in comments, identifiers, test names, or strings.
A comment states the engineering reason, never who or what suggested the change.

## Project conventions

- **No inline styles.** Styling goes in `.css` files. Never use CSS `color-mix()` — define explicit CSS
  custom properties instead.
- Use the `LoadingSpinner` component for every loader (`inline` prop inside buttons/text, `size` prop
  xs/sm/md/lg/xl).
- One icon per item. If a Card or section header already shows an icon, don't repeat it on buttons
  inside that section.
- `.tsx` files export React components ONLY — constants, types, and helpers move to their own file, or
  React Fast Refresh breaks.
- Strongly typed everywhere: real models and typed signatures, never loose `any`/untyped lambdas.
- Error handling follows the house standard in `docs/error-handling-standard.md` — read it before adding
  or changing error handling in C#, Rust, or the frontend.
- Adding a SignalR event means adding its name to `SIGNALR_EVENTS` in
  `Web/src/contexts/SignalRContext/types.ts`.
- Prefer reusing what exists over writing new code; justify any new helper that duplicates something
  already in the tree.

## Verification before you claim done

Build what you touched and report the actual result. Never report success you did not observe.

**C#** — always the whole solution, always non-incremental:

```
dotnet build lancache-manager.sln --no-incremental -p:SkipRustBuild=true \
  -warnaserror:CS8600,CS8601,CS8602,CS8603,CS8604
```

Then check the output for `warning CA`, `warning IDE`, `warning RCS` and fix every one. Both flags
matter: **an incremental build of an already-built project skips compilation and re-emits zero
analyzer warnings**, so a warning in a file you did not touch stays invisible locally while CI's
clean checkout always sees it. Building only the API csproj misses the Tests project, which CI
compiles and which fails on things a passing API build never shows.

**Frontend**: `npx tsc --noEmit`, `npm run lint`, and `npx vite build` when files moved or were deleted.
