# NyxilumLang

*[Українською](README.uk.md)*

A programming language built from scratch: a bytecode compiler, a stack-based
virtual machine, and a standard library of 166 built-in functions — everything
from math and strings to HTTP, graphics, and keyboard input.

**The language is self-hosted**: `selfhosted/` contains an interpreter for
NyxilumLang, written **in NyxilumLang itself**, capable of running programs
with recursion, structs, methods, arrays, and closures.

```nx
func factorial(n) {
    if (n <= 1) { return 1 }
    return n * factorial(n - 1)
}

func makeCounter() {
    var count = 0
    return func() {
        count = count + 1
        return count
    }
}

func main() {
    print(factorial(6))        // 720

    var counter = makeCounter()
    print(counter())           // 1
    print(counter())           // 2
}
```

## Quick Start

Prebuilt binary (no .NET required) — see [INSTALL.md](INSTALL.md), available
for Windows, Linux, and Mac. From source (requires the
[.NET SDK 10](https://dotnet.microsoft.com/download), cross-platform):

```bash
dotnet build src/NyxilumLang -f net10.0 -p:EnableWindowsTargeting=true
```

(The csproj multi-targets `net10.0-windows;net10.0` — without `-f net10.0
-p:EnableWindowsTargeting=true`, the build fails on Linux/Mac with NETSDK1100.)

Windows:

```powershell
powershell -ExecutionPolicy Bypass -File install-nx.ps1
```

Linux/Mac (no GUI/graphics — only Windows Forms supports those, the rest of
the language works the same everywhere):

```bash
bash install-nx.sh
```

Both scripts automatically locate the built binary in
`src/NyxilumLang/bin/.../publish/` and register the global `nx` command.
After that — open a new terminal window and run:

```bash
nx myprogram.nx
```

## Language Features

| Feature | Example |
|---|---|
| Variables, `null` | `var x = 10`, `var n = null` |
| String interpolation | `"Hello, ${name}! You are ${age} years old"` — any expression works inside `${...}` |
| Functions, recursion | `func add(a, b) { return a + b }` |
| Closures | `var f = func() { return count }` |
| Functions as values | `var op = sqrt`, `sort(arr, cmp)` |
| Structs and methods | `struct Point { x, y }`, `func Point.len() {...}` |
| Inheritance | `struct Dog extends Animal { ... }`, `super.method(...)`, polymorphism by default |
| Maps | `newMap()`, `mapSet`, `mapGet`, `mapKeys` |
| Arrays | `[1, 2, 3]`, `arr[0]`, `append(arr, 4)` |
| Loops | `for i in 0..10`, `for x in arr`, `while` |
| `break` / `continue` | work in all loops, including nested ones |
| Cyrillic in identifiers | `func привітати(імя) { ... }` |
| Errors | `try { ... } catch (e) { ... }`, `throw`, messages include the line number |
| Modules | `import "helpers.nx"`, selective `import "helpers.nx" { func1, func2 }` |
| Higher-order | `mapArr`, `filter`, `reduce`, `sort` |
| GC tooling | `gc_stats()`, `gc_collect()`, `gc_limit(n)` |
| Hot-loop optimization | automatic, can be disabled via `NX_JIT=0` |
| Files and directories | `deleteFile`, `makeDir`, `dirExists`, `deleteDir`, `listDir` |
| Archives | `zipCreate`, `zipExtract`, `zipEntries`, `zipExtractEntry` |
| Built-in database | `dbOpen`, `dbGet/dbSet/dbHas/dbDelete`, `dbKeys`, `dbCount` — persistent KV store with WAL |
| Concurrency | `spawn(fn, ...args)`/`workerJoin(w)` — isolated workers; `newChannel`/`channelSend`/`channelReceive` |
| External OS processes | `procStart`/`procRun` (launch), `procWait`/`procIsRunning`/`procKill`, `procOutput`/`procErrorOutput` — a real child process (not a worker) |

The standard library covers math, strings (including `trim`, `repeat`,
`indexOf`, `reverse`), arrays (`slice`, `unique`, `indexOf`, `reverse`),
maps, JSON, files, time, HTTP requests, 2D canvas graphics, keyboard
input, and GUI windows on Windows Forms with working buttons (`guiWindow`,
`guiButton`, `guiOnAction` — the click actually invokes a NyxilumLang function).

### External Processes

`System.Diagnostics.Process` under the hood, cross-platform (unlike GUI, it
works the same on Windows/Linux/Mac) — without it, writing something like a
launcher (start `java -jar ...` and monitor it) would have been impossible:

```nx
// Blocking - waits for completion and returns the result right away
var r = procRun("java", ["-version"])
print("code: " + toString(r.exitCode) + ", stderr: " + r.stderr)

// Non-blocking - for long-running processes (the game itself), the script keeps going in parallel
var game = procStart("java", ["-jar", "server.jar"])
while (procIsRunning(game)) {
    print(procOutput(game))   // everything the process has output SO FAR
    sleep(1000)
}
print("game exited with code " + toString(procExitCode(game)))

// procKill(game) - force-terminate (along with child processes)
```

Options as the third argument (`procStart`/`procRun`, like headers in
`httpRequest`) — a map with `cwd` (working directory) and `env` (a map of
environment variables for the process). In the sandbox (`NX_SANDBOX=1`),
launching processes is always forbidden, no exceptions — it's the broadest
access of all, since it bypasses both the filesystem and network restrictions.

### Files, Directories, and Archives

Previously, the only file operations available were
`readFile`/`writeFile`/`appendFile` — there was no way to delete a file,
create/list a directory, or extract an archive, even though Minecraft
versions, libraries, and mods are distributed exactly as `.zip`/`.jar` files:

```nx
makeDir("mods")
zipExtract("fabric-api.jar", "mods/fabric-api")   // extract the mod
var files = listDir("mods")                        // what's actually installed
deleteFile("mods/old-mod.jar")                      // delete one mod
deleteDir("instances/old-modpack")                  // delete an entire modpack
```

`zipEntries(zipPath)` returns the list of files inside an archive WITHOUT
extracting it — handy for pulling just the `.dll`/`.so` files you need out
of a natives jar via `zipExtractEntry`, without touching the rest.
`zipCreate(zipPath, sourceDir)` — packs a directory back up (e.g. exporting
a modpack).

### Memory and GC

NyxilumLang values are boxed CLR objects, so garbage collection (including
reference cycles in structs) is already correctly handled by the .NET CLR
itself. Instead of duplicating that, the runtime gives NyxilumLang scripts
their own allocation accounting (arrays/structs/maps) and an optional limit,
so a runaway allocation loop can't take down the host process:

- `gc_stats()` → returns the struct `{ allocated, limit, bytesEstimate }`
- `gc_collect()` → forces `GC.Collect()` and refreshes the memory estimate
- `gc_limit(n)` → sets the allocation-count limit for the current run; exceeding it throws an error that can be caught with `try/catch`

The limit can also be set externally without changing the code:
`NX_GC_MAX_OBJECTS=10000 nx script.nx`.

### Hot-Loop Optimization (JIT)

NyxilumLang values are boxed CLR objects, so "real" compilation to machine
code would only win on switch dispatch, not on the boxing and dictionary
lookups that actually dominate the cost — meaning it's not worth the
complexity and risk for a language without static value types. Instead, the
VM recognizes two specific, safe "hot" sequences in the bytecode (a loop
counter `i = i + constant` and a counter compared against a bound), and after
~512 iterations executes them directly via C# arithmetic instead of stack
dispatch — with a type check on every iteration, so a type change inside the
loop immediately and safely falls back to the regular interpreter. Loops with
`try/catch`/`throw` in the body are never optimized at all (a safe bailout).

Measured on a Release build (a loop of 20 million iterations): ~30s with the
optimization enabled versus ~58s without it. It can be disabled with `NX_JIT=0`.

### Sandbox for Untrusted Code

By default, a `.nx` script has full access to the filesystem, network, and
environment variables — just like a regular Python/Node.js script. If
NyxilumLang code is run by another service on the user's behalf (for
example, [NyxilumMcp](https://github.com/Faneraiy14/NyxilumMcp) executes
potentially AI-generated code), turn on the restricted mode with a flag:

```bash
NX_SANDBOX=1 nx script.nx
```

In this mode:

- `readFile`/`writeFile`/`appendFile`/`fileExists`/`readLines`/`deleteFile`/`makeDir`/`dirExists`/`deleteDir`/`listDir`/`zipExtract`/`zipCreate`/`zipExtractEntry`/`zipEntries` are only allowed within the current working directory — escaping it via an absolute path (`/etc/passwd`), `../..`, or a symlink (a file or directory inside the working directory that points outside it) throws an error
- `httpGet`/`httpPost`/`httpRequest`/`httpServer`/`urlStatus`/`wsConnect` throw an error — the network is fully disabled
- `osEnv` throws an error — environment variables (where tokens/keys might live) are inaccessible
- `procStart`/`procRun` throw an error — launching external processes is completely forbidden (the broadest access of all: an arbitrary executable bypasses both the filesystem and network restrictions above)
- `guiWindow` throws an error — opening an ACTUAL window on the user's screen is forbidden (this applies both to Windows Forms and the X11 version on Linux/Mac)

The flag is off by default — no regular script that reads files outside its
own folder or talks to the network will break, unless someone explicitly
asks for the restricted mode.

### Built-in Database

[NyxilumDb](https://github.com/Faneraiy14/NyxilumDb) — a sister project, an
embedded KV store with WAL durability — is wired in as part of the stdlib:

```nx
var db = dbOpen("mydata")
dbSet(db, "greeting", "Hello!")
print(dbGet(db, "greeting"))
dbClose(db)
```

Values in v1 are strings only (UTF-8). Full list: `dbOpen(path)`,
`dbClose(db)`, `dbSet(db,k,v)`, `dbGet(db,k)`, `dbHas(db,k)`,
`dbDelete(db,k)`, `dbKeys(db,prefix?)`, `dbCount(db)`,
`dbCheckpoint(db)`. Data survives a process restart — the WAL and
compaction are described in the NyxilumDb README.

### VS Code

Syntax highlighting extension for `.nx` — in [vscode-nyxilum/](vscode-nyxilum/README.md).

The full syntax reference is in [GUIDE.md](GUIDE.md).

## How It Works

```
Source code (.nx)
      │
   Lexer.cs        tokenization
      │
   Parser.cs       recursive descent -> AST
      │
   Compiler.cs     AST -> bytecode (61 opcodes)
      │
VirtualMachine.cs  stack-based VM executes the bytecode
```

This is **not** a tree-walking interpreter: the program is first compiled
into bytecode, and the VM then executes that bytecode using an operand
stack, a stack of local-variable frames, and its own stack of `try/catch`
handlers.

```
src/NyxilumLang/
  Core/       Lexer.cs, Parser.cs, Token.cs
  AST/        tree nodes
  Compiler/   Compiler.cs, Bytecode.cs
  VM/         VirtualMachine.cs — execution + 166 built-in functions
  Runtime/    NxMap, NxJson, NxFunctionRef, Http/Os/Graphics/Regex/WebSocket modules
  Tools/      Formatter.cs, Linter.cs

selfhosted/   NyxilumLang interpreter, written in NyxilumLang
bootstrap/    early minimal self-host
tests/        tests + run_all.sh
programs/     examples (nx_dashboard — a system dashboard, guess_the_number — a "guess the number" game)
lib/          standard library of .nx modules (strings, collections, datetime, testing, http_client, telegram, discord) — pulled in via import
```

## Commands

| Command | What it does |
|---|---|
| `nx file.nx` | run the file |
| `nx` | REPL — line-by-line execution, `exit()` to quit |
| `nx install owner/repo` | install a package, add it to `nx.json` |
| `nx install` | install everything from `nx.json` |
| `nx uninstall name` | remove a package from `nx.json` and delete it from `nx_modules/` |
| `nx update` | update all packages to each one's current default branch |
| `nx update name` | update only one package |
| `nx format file.nx` | format a file (prints to the console) |
| `nx lint file.nx` | check a file for common mistakes |
| `nx check file.nx` | check syntax only (without running the code) |
| `nx --version` | print the version |

Every detail about packages is in the ["Package Manager"](#package-manager)
section below; for installing `nx` itself, see [INSTALL.md](INSTALL.md).

## Package Manager

There's no "official registry" of packages — a package is just any public
GitHub repository with a `main.nx` at its root. To install:

```bash
nx install owner/repo
```

Pulls the repository down, drops it into `nx_modules/<repo>/`, and appends
an entry to `nx.json` next to your file. What gets recorded isn't the
branch name, but the exact commit SHA it pointed to at install time
(SHA pinning):

```json
{ "dependencies": { "repo": "owner/repo@4b99d80c38cbbbff2abfe957eb869efc47452ffa" } }
```

This makes installs reproducible: a later `nx install` always fetches the
exact same byte-for-byte content, even if the package's branch has since
been updated or force-pushed. You can specify a particular branch/tag/commit
at install time (`nx install owner/repo@dev`) — it will still get pinned as
a SHA in `nx.json` right after installing.

Without an argument, `nx install` installs everything from `nx.json` — the
same way `npm install` with no package name installs everything from
`package.json`.

To use it — import without the `.nx` extension:

```nx
import "repo"

func main() {
    print(someFunctionFromThePackage())
}
```

Package resolution walks up from the current file's directory to the disk
root — the same way Node.js looks for `node_modules`.

SHA pinning makes `nx install` reproducible, but as a consequence it will
never pick up a package's new commits on its own — there's a separate
command for that:

```bash
nx update          # all packages from nx.json
nx update owner-repo # just one, by name (as it appears in nx.json)
```

Re-resolves `owner/repo` against its current default branch and overwrites
the SHA pin. If the package was installed from a specific branch/tag
(`owner/repo@dev`), `update` will still go to the default branch — to
update within that same branch instead, it's simplest to just re-run
`nx install owner/repo@dev`.

To remove a package — `nx uninstall name` deletes the entry from `nx.json`
and the directory itself from `nx_modules/`.

## Real-World Applications

Proof that the language can handle more than toy examples — real working
applications:

- **[nyxilum-paste](https://github.com/Faneraiy14/nyxilum-paste)** — a
  text-snippet sharing service (like Pastebin), with zero external
  libraries or other languages under the hood — just `httpServer` and
  `dbOpen` from the language's own standard library.
- **[nyxilum-control-center](https://github.com/Faneraiy14/nyxilum-control-center)**
  — a live system monitor: a web dashboard with metrics (memory, CPU, the
  VM's own GC state), pushed over WebSocket every 2 seconds with no page
  reload.
- **[nyxilum-chat](https://github.com/Faneraiy14/nyxilum-chat)** — a live
  group chat: real one-to-many WebSocket broadcasting (not just a
  single-viewer push like control-center) — message history stored in
  NyxilumDb, with each connection polling and sending only what's new.
  Verified with a live test using two simultaneous clients.

## Self-Hosting

`selfhosted/` is proof the language is complete enough to describe itself:

| File | What it does |
|---|---|
| `lexer.nx` | tokenization |
| `parser.nx` | recursive descent, AST represented as maps |
| `interpreter.nx` | AST walking, chained environments, closures |
| `main.nx` | entry point, runs the guest program |

```bash
cd selfhosted
nx main.nx
```

Expected output: `720 / 5 / 100 / 30 / 1 2 3 / 256 / ПРИВІТ` — recursion, a
struct with a method, arrays, indexing, a closure-based counter mutating
captured state, a native call, and string handling.

Two interesting design choices inside:

- **Environments are a chain of maps** `{__vars, __parent}`. Since a map is
  a reference type, closures share one mutable state — which is exactly why
  the counter actually counts instead of returning one every time.
- **`return` is implemented via `throw`/`try`**: the value is wrapped in an
  `__isReturn` marker and propagated up to the nearest function call. The
  marker is critical — without it, `catch` would also swallow real errors,
  silently turning them into a function's return value.

## Tests

```bash
bash tests/run_all.sh
```

51 tests: recursion, closures, frames, maps, methods, the language's
standard library, `try/catch`, modules (both plain and selective import),
`lib/testing.nx`, `lib/strings.nx`, `lib/collections.nx`, `lib/datetime.nx`,
self-hosting, `break`/`continue`, global variables, numeric equality,
parenthesized conditions, calling the result of a call directly,
fractional-part truncation (`toInt`, array indices), nested named functions
with lexical visibility, GC tooling, regex, JIT (byte-for-byte identical
output with NX_JIT=0/1), the built-in database (NyxilumDb), concurrency
(`spawn`/`workerJoin`/channels, including a data-race stress test), external
OS processes (`procRun`/`procStart`/`procKill`), files/directories/archives
(`deleteFile`/`makeDir`/`listDir`/`zipCreate`/`zipExtract`). Graphics tests
and the HTTP server are skipped automatically — they open windows/listen on
a port forever.

Tests where an error is the **expected** outcome (e.g. an unhandled
`throw`) are listed in `EXPECT_ERROR` inside the script: for those, the
absence of an error is what counts as a failure.

## The nx Command

Installed together with the language — `install-nx.ps1` from a prebuilt
release or from a repo clone, details in [INSTALL.md](INSTALL.md). After
installation, from any folder:

```bash
nx myprogram.nx
```

## Known Limitations

- A named `func` can be declared inside another function — it's visible
  only within that "parent" (and deeper, in further-nested funcs),
  lexically, as in most languages. The same name in different "parents"
  doesn't conflict — each calls its own. This is **not a closure**: the
  nested function can't see the parent's local variables (for that, use
  anonymous lambdas — `var f = func() {...}` — which do capture their
  environment).
- GUI (`guiWindow`, ...) and 2D/3D graphics (`createCanvas`, ...) only work
  on Windows (Windows Forms). On Linux/Mac these functions aren't
  available — calling them gives a clear runtime error rather than
  crashing. The rest of the language (everything except GUI/graphics) is
  cross-platform, built with the `net10.0` target.
- GUI functions that touch an already-created control (`guiSetText`,
  `guiGetText`, `guiAdd`, `guiShow`, `guiOnAction`, `presentCanvas`,
  `closeCanvas`) can't be called from a worker (`spawn`) — Windows Forms
  requires that a control only be touched by the thread that created it.
  Calling one from a worker throws a clear error instead of an
  unpredictable crash or corrupted window state. If a worker needs to
  update the GUI based on its computation, pass the result back via
  `workerJoin`/a channel and update the GUI itself from the main thread.

## License

MIT — see [LICENSE](LICENSE). Author: Faneraiy14.
