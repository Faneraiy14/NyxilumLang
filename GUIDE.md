
# NyxilumLang: User Guide

*[Українською](GUIDE.uk.md)*

NyxilumLang is a powerful yet simple programming language, built for learning and rapid development.

## Basic Syntax

### Variables
Use `var` to declare variables.
```nx
var x = 10
var name = "Nyxilum"
var isCool = true
var nothing = null       // isNull(nothing) -> true
```

### Strings and Interpolation
Concatenation with `+` works with any type — numbers/bools/null are
automatically converted to a string:
```nx
var age = 20
print("I am " + age + " years old")   // I am 20 years old
```

For more complex strings, interpolation `${expr}` is more convenient —
inside can be any NyxilumLang expression (variable, arithmetic, function call):
```nx
var name = "Anna"
var age = 20
print("Hello, ${name}! You are ${age} ${age + 1 - 1} years old.")
print("Doubled: ${age * 2}")
```
This isn't a separate runtime feature — the compiler expands `"a${b}c"`
into plain `"a" + b + "c"` at the lexer stage, so it works anywhere `+`
does, with no restrictions.

To keep `${` from being treated as the start of an interpolation and leave
it as plain text — escape the dollar sign: `\${`.
```nx
print("Price: \${not interpolation}")   // Price: ${not interpolation}
```

### Functions
Functions are declared using `func`. The entry point is the `main` function.
```nx
func add(a, b) {
    return a + b
}

func main() {
    var result = add(5, 10)
    print("Result: " + result)
}
```

A `func` can also be declared inside another function — visible only
within that "parent" (lexically, as in most languages): the same name in
different "parents" doesn't conflict, each calls its own. This is not a
closure — the nested function can't see the parent's local variables;
capturing the environment requires an anonymous lambda (`var f = func()
{...}`, section below).

```nx
func outer() {
    func inner(x) {
        return x * 2
    }
    return inner(21)
}
print(outer()) // 42
```

### Control Flow
```nx
if (x > 0) {
    print("Positive")
} else {
    print("Negative or zero")
}

for i in 0..5 {
    print(i)
}

for item in [10, 20, 30] {
    print(item)     // iterates over array elements (no manual index needed)
}

while (x > 0) {
    print(x)
    x = x - 1
}
```

### Structs and Methods
```nx
struct Point {
    x: i32
    y: i32
}

func Point.move(dx, dy) {
    self.x = self.x + dx
    self.y = self.y + dy
}

func main() {
    var p = Point { x: 10, y: 20 }
    p.move(5, 5)
    print(p.x) // 15
}
```

Struct fields must have a type (`x: i32`, not just `x`), a method is
`func Struct.method(...)` declared separately from the `struct` block,
and access to the current instance is `self`, not `this`.

### Inheritance (extends, super)
```nx
struct Animal {
    name: string

    func speak() {
        print(self.name + " makes some kind of sound.")
    }

    // self.speak() here will find the most DERIVED override —
    // polymorphism: dispatch always follows self's ACTUAL type,
    // not wherever the calling method happens to be declared.
    func introduce() {
        print("This is " + self.name + ".")
        self.speak()
    }
}

struct Dog extends Animal {
    func speak() {
        super.speak()          // call the parent implementation
        print(self.name + " barks: Woof!")
    }
}

func main() {
    var dog = Dog { name: "Rex" }
    dog.introduce()
    // This is Rex.
    // Rex makes some kind of sound.
    // Rex barks: Woof!
}
```

`extends Parent` gives a struct access to all of the ancestor's methods
(and its ancestors' ancestors — the chain is unlimited) that it doesn't
override itself. An overridden method fully replaces the parent's for
instances of the child struct; `super.method(...)` inside an override
calls SPECIFICALLY the parent implementation, bypassing the override
(otherwise you'd get infinite recursion).

What's deliberately NOT there (to avoid bloating the language): access
modifiers (`private`/`public` — all fields and methods are public),
multiple inheritance, abstract structs/interfaces. Ancestor and descendant
fields are just keys in the same dictionary — there's no separate
declaration of "inherited fields": `Dog { name: "Rex" }` already gets
everything both Dog's own methods and the ones inherited from Animal expect.

### Error Handling (try/catch/throw)
```nx
func riskyDivide(a, b) {
    if b == 0 {
        throw "Division by zero!"
    }
    return a / b
}

func main() {
    try {
        var r = riskyDivide(10, 0)
    } catch (e) {
        print("Caught: " + e)
    }
}
```
`try`/`catch` catches both an explicit `throw` and internal VM errors
(out-of-bounds array access, native function errors, etc.) — the program
doesn't crash, execution just continues from the catch block.

### Maps/Dictionaries
```nx
var ages = newMap()
mapSet(ages, "Sviatoslav", 14)
print(mapGet(ages, "Sviatoslav"))       // 14
print(mapHas(ages, "Bohdan"))          // false
mapRemove(ages, "Sviatoslav")
var keys = mapKeys(ages)
```
A map is a distinct type from `struct` (`typeOf()` returns `"map"`), and
a key can be a number, string, or bool.

### Functions as Values and Closures
```nx
func square(x) { return x * x }

func apply(fn, x) { return fn(x) }

func main() {
    print(apply(square, 5))              // 25 - named function as a value

    var double_ = func(x) { return x * 2 }
    print(double_(21))                   // 42 - anonymous function (lambda)

    var makeAdder = func(n) {
        return func(x) { return x + n }  // closure - captures n
    }
    var add5 = makeAdder(5)
    print(add5(10))                      // 15
}
```
Closures capture outer variables' values **as a copy at the moment the
lambda is created** (not a live reference) — changing the outer variable
after the lambda is created has no effect on it.

### Modules (import)
```nx
// math_helpers.nx
func square(x) { return x * x }
```
```nx
// main.nx
import "math_helpers.nx"

func main() {
    print(square(5))   // 25
}
```
The path in `import` is relative to the importing file. Circular and
repeated imports are safe (each file is processed once).

Selective import — pulls in only the listed functions/structs/global
variables, not the whole file:
```nx
import "math_helpers.nx" { square }

func main() {
    print(square(5))   // 25 - cube() from the same file stays unimported
}
```
A named struct's methods are pulled in automatically along with it. If a
listed name doesn't exist in the file — that's an error right at startup
(before the code even runs). Functions whose name starts with `_` (e.g.
`_helper`) are treated as the module's private helpers and are always
pulled in, even if not listed — so a public function from the same file
can call its internal helper regardless of what the caller actually imports.

### Standard Library (`lib/`)
The `lib/` directory at the repo root contains ready-made `.nx` modules —
pulled in with a regular `import` using a relative path (`../lib/...` from
a file in `tests/`, or `lib/...` if the script sits next to `lib/` itself):

- **`lib/datetime.nx`** — date arithmetic with correct leap years (Howard Hinnant's algorithm, pure NyxilumLang): `daysFromCivil(y,m,d)`/`civilFromDays(z)` (date <-> days since epoch), `isLeapYear(y)`, `dayOfWeek(y,m,d)` (0=Sunday), `dayName(weekday)`, `addDays(y,m,d,n)`, `diffDays(y1,m1,d1,y2,m2,d2)`, `formatDate(y,m,d)`, `parseDate(s)`, `todayCivil()`.
- **`lib/strings.nx`** — `capitalize(s)`, `titleCase(s)`, `isBlank(s)`, `isEmpty(s)`, `padLeft(s, len, ch)`, `padRight(s, len, ch)`, `countOccurrences(s, sub)`.
- **`lib/collections.nx`** — `range(n)`, `rangeFrom(start, end)`, `sum(arr)`, `first(arr)`, `last(arr)`, `flatten(arr)` (one level), `zip(arr1, arr2)`, `chunk(arr, size)`, `count(arr, fn)`.
- **`lib/testing.nx`** — `assertTrue(cond, msg)`, `assertFalse(cond, msg)`, `assertEqual(actual, expected, msg)`, `assertThrows(fn, msg)`. A failure is just a regular `throw`, so either catch it yourself with `try/catch`, or leave it uncaught so the process exits with a non-zero code (handy for CI).
  ```nx
  import "lib/testing.nx" { assertEqual }

  func main() {
      assertEqual(2 + 2, 4, "2+2 should equal 4")
      print("test passed")
  }
  ```
- **`lib/http_client.nx`** — a wrapper over `httpGet`/`httpPost`/`httpRequest` with automatic JSON serialization (in the spirit of Python's `requests` library): `getJson(url)`, `postJson(url, data)`, `requestJson(url, method, data)` (`data` — a map/array or `null`), `requestStatus(url, method, data)` — status code only.
  ```nx
  import "lib/http_client.nx" { postJson }

  func main() {
      var data = newMap()
      mapSet(data, "name", "Sviatoslav")
      var response = postJson("https://httpbin.org/post", data)
      print(toJson(response))
  }
  ```
- **`lib/telegram.nx`** — a wrapper over the [Telegram Bot API](https://core.telegram.org/bots/api) (plain HTTPS+JSON, no WebSocket — so it's fully implemented in NyxilumLang itself): `tgGetMe(token)`, `tgSendMessage(token, chatId, text)`, `tgGetUpdates(token, offset)`, `tgMessageText(update)`, `tgChatId(update)`, and a blocking `tgPollLoop(token, handler)` for a ready-made bot in a single call. Read the token via `osEnv("TELEGRAM_BOT_TOKEN")`, never hardcode it in the script. Full working echo-bot example: `programs/telegram_echo_bot.nx`.
  ```nx
  import "lib/telegram.nx" { tgPollLoop, tgMessageText, tgChatId, tgSendMessage }

  func main() {
      var token = osEnv("TELEGRAM_BOT_TOKEN")
      tgPollLoop(token, func(update) {
          var text = tgMessageText(update)
          if !isNull(text) {
              tgSendMessage(token, tgChatId(update), "Echo: " + text)
          }
      })
  }
  ```
- **`lib/discord.nx`** — a minimal [Discord Gateway](https://discord.com/developers/docs/topics/gateway) client: `dSendMessage(token, channelId, text)` (REST), and a blocking `dPollLoop(token, intents, handler)` — handles the handshake itself (Hello -> Identify -> heartbeat on the server's schedule) via native `wsConnect`/`wsSend`/`wsReceive`, with `handler(eventName, data)` invoked on every event (`"MESSAGE_CREATE"`, `"READY"`, etc.). On a server-initiated disconnect (e.g. an invalid token — code 4004), it exits the loop with a clear message instead of hanging or crashing. Token via `osEnv("DISCORD_BOT_TOKEN")`. Full example: `programs/discord_echo_bot.nx`.
  ```nx
  import "lib/discord.nx" { dPollLoop, dSendMessage }

  func main() {
      var token = osEnv("DISCORD_BOT_TOKEN")
      dPollLoop(token, 37377, func(eventName, data) {
          if eventName == "MESSAGE_CREATE" {
              dSendMessage(token, mapGet(data, "channel_id"), "Echo: " + mapGet(data, "content"))
          }
      })
  }
  ```
  Don't forget to enable "Message Content Intent" in the Discord Developer Portal — without it, `content` is always empty.

### Higher-Order Functions and JSON
```nx
var nums = [5, 2, 8, 1]
var sorted = sort(nums, func(a, b) { return a - b })
var squares = mapArr(nums, func(x) { return x * x })
var evens = filter(nums, func(x) { return x % 2 == 0 })
var sum = reduce(nums, func(acc, x) { return acc + x }, 0)

var data = newMap()
mapSet(data, "name", "Sviatoslav")
print(toJson(data))              // {"name":"Sviatoslav"}
var back = fromJson("[1,2,3]")   // array
```

### Concurrency

`spawn(fn, ...args)` launches a function value on a brand-new, fully
isolated VM in a separate thread — its own stack, its own globals, the
same bytecode. There's zero shared mutable state with the caller or other
workers: everything that goes into `spawn()` (arguments, values, anything
captured by a lambda's closure) is deep-copied at the boundary — mutating
an array/map/struct on the main thread AFTER spawning has no effect on the
already-running worker, and vice versa.

```nx
func important(n) {
    return n * n
}

func main() {
    var w = spawn(important, 7)
    print(workerJoin(w))   // 49 — waits for completion and returns the result
}
```

`workerJoin(worker)` blocks the caller until the worker finishes, and
returns its result. If the worker terminated with an unhandled error,
`workerJoin` re-throws it, and it can be caught with a regular `try/catch`.

`workerJoin(worker, timeoutMs)` — with a wait-time limit: if the worker
doesn't finish in time, it throws an error (rather than returning `null` —
the worker could legitimately have returned `null` itself as a result, so
`null` doesn't work as a timeout marker). The worker itself does NOT
stop — we simply stop waiting for it; `workerJoin` can be called again
later to pick up the result once it does finish:

```nx
var w = spawn(slowTask)
try {
    print(workerJoin(w, 100))
} catch (e) {
    print("not ready yet, will try again later")
}
// ... other work ...
print(workerJoin(w))   // no timeout — wait for it to finish for good
```

Multiple workers at once:
```nx
var workers = []
var i = 0
while i < 10 {
    append(workers, spawn(important, i))
    i = i + 1
}
var results = []
i = 0
while i < len(workers) {
    append(results, workerJoin(workers[i]))
    i = i + 1
}
```

Channels — for communication BETWEEN workers (not just "start it and grab
the result at the end"):
```nx
var ch = newChannel()

func producer(c) {
    var i = 0
    while i < 5 {
        channelSend(c, i)
        i = i + 1
    }
}

func main() {
    var w = spawn(producer, ch)
    var i = 0
    while i < 5 {
        print(channelReceive(ch))   // blocks until the next message
        i = i + 1
    }
    workerJoin(w)
}
```
`channelReceive(ch, timeoutMs)` — with a timeout: returns `null` if
nothing arrived within the allotted time, instead of blocking forever.

Values passed through `channelSend` are also deep-copied — the same
"shared nothing" principle as `spawn()`.

**GUI/graphics from a worker.** `guiSetText`/`guiAdd`/`guiShow`/
`presentCanvas`/`closeCanvas` and similar (anything that touches an
already-created window) can only be called from the main thread — Windows
Forms requires this. Calling one from a worker throws a clear error
instead of crashing or corrupting the window's state. For a worker to
affect the GUI — have it return the result via `workerJoin` or send it
over a channel, and update the GUI itself from the main thread:

```nx
func heavyCalculation() {
    // ... long computation without any GUI ...
    return 42
}

func main() {
    var label = guiLabel("Calculating...", 10, 10, 200, 30)
    var w = spawn(heavyCalculation)
    var result = workerJoin(w)     // main thread waits and picks up the result
    guiSetText(label, toString(result))  // GUI is updated here, on the main thread
}
```

### Graphics (2D and 3D)
```nx
var canvas = createCanvas("Game", 400, 300)
var frame = 0
while frame < 60 {
    clearCanvas(canvas, 20, 20, 30)
    drawRect(canvas, 50, 100, 40, 40, 200, 60, 60)
    drawCircle(canvas, 300, 150, 25, 60, 200, 60)
    drawText(canvas, "Frame " + toString(frame), 10, 10, 14, 255, 255, 255)
    presentCanvas(canvas)
    sleep(16)
    frame = frame + 1
}
closeCanvas(canvas)
```
3D is done on top of the same canvas: rotating points via `sin`/`cos`
(already built in), and `project3D(canvas, x, y, z, camDistance)` turns a
3D coordinate into a 2D screen point — then you draw the edges with
`drawLine`. Input: `isKeyDown("W")`, `isMouseDown(canvas)`,
`getMouseX/Y(canvas)`, `canvasShouldClose(canvas)`.

### Built-in Functions
- `print(val)` - console output
- `readLine()`, `readInt()`, `readDouble()` - read from the console
- `sqrt(x)`, `sin(x)`, `cos(x)`, `tan(x)`, `pow(x,y)`, `abs(x)`, `round/floor/ceil(x)`, `clamp(x,min,max)` - math
- `max/min(a,b)` or `max/min(arr)` - maximum/minimum of two numbers or of an entire array
- `toFixed(x,n)` - number as a string with n digits after the decimal point, e.g. `toFixed(3.14159, 2)` -> `"3.14"`
- `readFile(path)`, `writeFile(path, content)`, `appendFile(path, content)`, `fileExists(path)`, `readLines(path)` - file operations
- `deleteFile(path)`, `makeDir(path)`, `dirExists(path)`, `deleteDir(path)` (recursive), `listDir(path)` (array of file/directory names inside) - files and directories
- `zipCreate(zipPath, sourceDir)` - packs a directory into an archive; `zipExtract(zipPath, destDir)` - extracts the whole archive, returns the file count; `zipEntries(zipPath)` - list of filenames inside WITHOUT extracting; `zipExtractEntry(zipPath, entryName, destPath)` - extracts just one file, returns `true`/`false` (does that entry exist in the archive?)
- `toString(v)`, `toInt(v)`, `toDouble(v)`, `typeOf(v)`, `isNumber/isString/isArray/isBool(v)` - type conversion and checking. Numeric strings are always parsed with "." as the decimal separator regardless of the OS locale `Nx` runs under (the process forcibly starts with `CultureInfo.InvariantCulture`) - `toDouble("0.083")` gives the same result on an en-US machine as on a uk-UA one
- `charCode(s)` - the code of a string's first character (e.g. `charCode("A")` -> 65); `fromCharCode(code)` - the character for a given code
- `len(v)`, `substring(s,start,len)`, `replace/toUpper/toLower/contains/startsWith/endsWith(s,...)`, `split(s,sep)`, `join(arr,sep)` - strings
- `trim(s)` - strips whitespace from both ends; `repeat(s,n)` - repeats the string n times
- `utf8ByteCount(s)` - the string's real size in UTF-8 bytes. `len(s)` counts characters (.NET `string.Length`, UTF-16 code units), NOT bytes - for Cyrillic/emoji that's almost half the size the string actually takes up on disk or over the network (everything is encoded as UTF-8). A size limit like `if (len(text) > MAX_BYTES)` will systematically let through twice as much Cyrillic text as intended - use this function specifically for a real byte-based limit
- `indexOf(v,item)` - the position of an element/substring (-1 if not found); works for both strings and arrays
- `reverse(v)` - reverses a string or array (doesn't mutate the original array)
- `append(arr,v)`, `pop(arr)`, `insert(arr,i,v)`, `removeAt(arr,i)`, `clear(arr)` - arrays
- `slice(arr,start,end?)` - a sub-array from start to end (or to the end); `unique(arr)` - removes duplicates, keeps order
- `newMap()`, `mapSet(m,k,v)`, `mapGet(m,k)`, `mapHas(m,k)`, `mapRemove(m,k)`, `mapKeys(m)`, `mapValues(m)` - maps/dictionaries
- `sort(arr,cmp)`, `mapArr(arr,fn)`, `filter(arr,fn)`, `reduce(arr,fn,init)` - higher-order functions over arrays
- `toJson(v)`, `fromJson(str)` - serialization to JSON and back
- `sleep(ms)` - pauses execution
- `exit(code)` - immediately terminates the process with the given exit code (lines of code after exit() are never executed) - for CI/scripts that need a specific code (0 for success, anything else for failure), without an artificial throw (which always yields code 1)
- `gc_stats()` - the struct `{allocated, limit, bytesEstimate}` tracking NyxilumLang allocations (arrays/structs/maps) for the current run; `gc_collect()` - forces .NET garbage collection and refreshes the memory estimate; `gc_limit(n)` - sets the allocation-count limit, exceeding it throws an error (can be caught with `try/catch`, or set externally via `NX_GC_MAX_OBJECTS`). Does NOT protect against infinite recursion that allocates nothing (no arrays/structs/maps) - there's a separate call-depth limit for that below.
- Call depth is capped at 10,000 (func, struct method, function value) - a function with no exit condition (e.g. `func f(n) { return f(n+1) }`) throws a `try/catch`-catchable error instead of unbounded memory growth all the way to OOM
- Nesting depth of expressions/blocks and operator-chain length are capped (500) - overly deep parentheses/calls/`if` blocks, or long chains of `+`, `!`, `=`, etc. throw a Runtime Error already at the file-parsing stage, rather than a native stack overflow (which can't be caught by any try/catch, neither in NyxilumLang nor in .NET)
- `dbOpen(path)` - opens (or creates) a persistent [NyxilumDb](https://github.com/Faneraiy14/NyxilumDb) KV store in the directory at `path`; `dbSet(db,k,v)`, `dbGet(db,k)` (string or `null`), `dbHas(db,k)`, `dbDelete(db,k)` - values in v1 are strings only; `dbKeys(db,prefix?)` - array of keys (optionally filtered by prefix); `dbCount(db)`; `dbCheckpoint(db)` - forces compaction; `dbClose(db)` - closes the database (compacts the WAL if it's non-empty)
- `createCanvas(title,w,h)`, `clearCanvas`, `drawRect`, `drawCircle`, `drawLine`, `drawText`, `presentCanvas`, `canvasShouldClose`, `closeCanvas` - 2D graphics
- `project3D(canvas,x,y,z,camDistance)` - projects a 3D point into 2D for rendering 3D scenes
- `guiWindow(title,w,h)`, `guiLabel(text,x,y,w,h)`, `guiButton(text,x,y,w,h)`, `guiTextBox(x,y,w,h)` - Windows Forms windows
- `guiAdd(parent,child)` - adds an element to a window; `guiSetText(control,text)`/`guiGetText(control)` - set/read text
- `guiOnAction(button,fn)` - invokes the NyxilumLang function `fn` on click (a plain function value, like in `sort(arr,cmp)` — no parentheses, not a name string)
- `guiShow(win)` - shows the window (blocks until it's closed)
- `isKeyDown(key)`, `isMouseDown(canvas)`, `getMouseX/Y(canvas)` - input for a window
- `randomInt(min,max)`, `randomDouble(min,max)`, `now()`, `today()`, `timestamp()` - utilities
- `formatDate(timestamp, format?)` - converts a Unix timestamp (seconds) into a string in any format (.NET custom date format, e.g. `"dd.MM.yyyy HH:mm"`); without `format` - the same look as `now()`; `parseDate(str, format)` - the reverse operation, a formatted string back into a Unix timestamp; throws an error if the string doesn't match the format
- `osPlatform()`, `osArchitecture()`, `osMemory()`, `osCpuCount()`, `osEnv(name)`, `osCwd()` - system information
- `osProcessList()` - array of every OS process (not just child ones like `procRun`/`procStart`), each `{pid, name, memMB, cpuPercent}`; `cpuPercent` is computed the way `top`/`htop` do it - two `TotalProcessorTime` samples with a ~200ms pause between them, so the call isn't instant. Processes that exited between the two samples are simply skipped, they don't fail the call
- `osDiskFree(path?)` - `{freeGB, totalGB}` for the volume containing `path` (defaults to the current directory)
- `notify(title, message)` - a native OS push notification (not the app's own window - a real bubble on top of everything else, like in messenger apps): `notify-send` on Linux, `osascript` on macOS, a `NotifyIcon` balloon via PowerShell on Windows
- `procStart(cmd, args?, options?)` - launches an external OS process and returns a handle right away, NOT waiting for completion (`args` - array of strings, `options` - a map with `cwd`/`env`); `procRun(cmd, args?, options?)` - the same, but BLOCKS until completion and returns the map `{exitCode, stdout, stderr}`; `procWait(h)` - waits for an already-started `procStart` to finish, returns the exit code; `procIsRunning(h)`, `procKill(h)`, `procPid(h)`, `procExitCode(h)` (`null` while the process is still running); `procOutput(h)`/`procErrorOutput(h)` - everything the process has written to stdout/stderr SO FAR (can be re-read repeatedly while the process is still running). Always forbidden in the sandbox (`NX_SANDBOX=1`)
- `httpGet(url)`, `urlStatus(url)` - HTTP requests
- `httpPost(url, body)` - a POST request with body `body` (Content-Type `application/json`), returns the response body as a string
- `httpRequest(url, method, body?, headers?)` - a request with an arbitrary method (`"PUT"`, `"DELETE"`, `"PATCH"`, etc.), returns the map `{status, body}`; `headers` - a map (`newMap`/`mapSet`) for headers like `Authorization`
- `wsConnect(url)` - opens a WebSocket connection (`wss://`/`ws://`); `wsSend(ws, text)` - sends a text message; `wsReceive(ws, timeoutMs?)` - blocks until the next message or the timeout (in which case it returns `null`, and the connection itself remains fully usable - you can keep calling wsSend/wsReceive/wsClose as normal); throws an error (catch with `try/catch`) if the connection was closed by the server; `wsClose(ws)` - closes the connection (5s timeout waiting for a close frame in response - if the other side doesn't respond, the socket is closed locally regardless, instead of blocking the script forever)
- `httpServer(port, handler, wsHandler?)` - an HTTP server; `handler(request)` is invoked for every regular request with a SINGLE map `{path, method, body, query, headers}` (`body` - the POST/PUT request body, `headers` - a map of request headers). The response is either a plain string (status 200, `text/html`) or a map `{status?, body?, contentType?}` for full control. The optional third argument `wsHandler(ws, request)` - requests with `Upgrade: websocket` are accepted as WebSocket connections (the same `ws` as in `wsConnect()` - the same `wsSend`/`wsReceive`/`wsClose` work on it); each connection runs on its own thread with its own VM, so a long-lived WS doesn't block other clients from connecting. Blocks forever (Ctrl+C to stop)
- `regexTest(s, pattern)` - whether the string matches the regex pattern (bool); `regexMatch(s, pattern)` - the first match or `null`; `regexFindAll(s, pattern)` - array of all matches; `regexReplace(s, pattern, replacement)` - replaces all matches
- `guiWindow(title, w, h)`, `guiButton(text, x, y, w, h)`, `guiShow(win)` - GUI (experimental)

## How to Run
After installation (see INSTALL.md), the `nx` command is available on any
platform (Windows/Linux/Mac) — pass it a file path:
`nx program.nx`

Other commands:
- `nx format program.nx` - print formatted code
- `nx lint program.nx` - check the code for common mistakes (unused variables, overly long lines, empty blocks)
- `nx check program.nx` - Lexer+Parser only (no Compiler/VM) - "is the syntax even valid", with no side effects; prints `OK` or `Parse Error: ...`
- `nx ast program.nx` - the AST in the canonical JSON schema `{"type","line","attributes","children"}` (`AstJsonDumper.cs`) - the same format that `PhpProvider` in [anylint](https://github.com/Faneraiy14/anylint) emits from the `nikic/php-parser` tree, so the analyzer's structural rules (dead-code-after-return, empty-catch) work on `.nx` files with zero changes to their own code, via `NyxilumProvider`
