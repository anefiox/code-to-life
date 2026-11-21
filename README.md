# Code to Life

See a C# codebase as a living map: every class and how it connects, the methods inside and who calls them, and, while it runs, where the program actually is.

Point it at a solution, project or folder and it opens a browser page with:

- **Learn a new codebase.** It opens on a **Folders** map: one box per folder, with what that kind of folder is usually for and how the folders lean on each other. **★ Start here** is a guided reading order, from where the program starts along the code it leads to, then the controllers and pages ASP.NET Core finds by itself, then the types everything uses. The **Pages** tab lists every address the app answers (minimal APIs, controller actions, Razor Pages) and shows what happens when one is visited: the middleware, the route, the handler, the code it calls and the Razor view it fills in.
- **Explanations in plain English.** Every class and method gets a "What it does" section, written by Claude through the MCP server or by an AI you point Code to Life at (OpenRouter or your own OpenAI-compatible server). They're kept in `.codetolife/explanations.json` and flagged when the code changes. The packages a project and each file use are described too.
- **The map.** Every type, grouped by namespace, joined by how they really relate: inherits, implements, *has* (a field or property holds it), *calls*, *creates* (`new`), and optionally *mentions*. Click a class for its members, dependencies, users and source. Double-click to open it up and see its methods calling each other and into other classes. Right-click a method to follow its calls a few levels deep.
- **The debugger, drawn on the map.** Start your program (or attach to one) and step through it. Whenever it pauses, the call stack lights up across the graph as a trail of glowing nodes and moving arrows, with locals and the current line beside it. **Animate** keeps stepping on its own so you can watch execution flow through the classes. Click the margin of the source view, or a method's ● button, for breakpoints. **Run to here** on any method.
- **Live view, no debugger needed.** Pick any running .NET process and Code to Life samples its stacks several times a second (without pausing it) and heats up the code that's running. Calls that only exist at runtime, like the concrete class behind an interface, appear as new cyan edges. It also listens to the runtime's own event stream, so time spent **awaiting** shows up in blue, with exceptions thrown, the methods allocating most, and outgoing HTTP calls. **Count instances** shows how many objects of each class are alive on the heap.
- **Which tests reach this?** Every test (`[Fact]`, `[Test]`, `[TestMethod]`…) is followed through the call graph, interfaces included. Color the map by **test coverage**, see the tests behind any method, run just those, and in **What changed** get the tests to run for your branch and the changed code nothing tests.
- **Impact of a change.** **⚠ Impact** on any class or method lists what would need to change or be checked: callers by distance, implementations and overrides, the interfaces it fulfills, the types that hold or create it, the tests that reach it, and what the compiler won't catch (reflection by name, string mentions, DI registrations, serialization attributes). Highlight it on the map, copy it as a checklist, or add it to the plan.
- **Architecture rules.** Write down the design you mean to keep ("Domain must not depend on Infrastructure") in `.codetolife/rules.json`. Broken rules show as red edges, and `codetolife check` fails CI when one breaks.
- **Stack traces and logs on the map.** Paste an exception from a bug report or CI and its path lights up like the debugger's stack. Point it at a log file (plain text or JSON lines) and the failures are grouped by cause, with the classes they pass through heated up.
- **An MCP server for AI assistants.** `codetolife-mcp` gives Claude (or any MCP client) the same graph: who calls what through interfaces, impact, tests, rules, stack-trace mapping, and a debugger it can drive itself.
- **What changed.** Uses [PR Buddy](https://github.com/anefiox/pr-buddy)'s semantic diff to color the members your branch really changed (behavior, signature, added, removed), ignoring reformatting, with PR Buddy's findings pinned to the code they're about.
- **Plan mode.** Sketch new types on top of the real graph, connect them to existing classes, leave notes on anything, and turn the sketch into C# stubs. The plan is saved in `.codetolife/plan.json` beside your code, so it can be committed and reviewed.
- **Color by** kind, project, complexity, coupling, test coverage, changes, incidents from a log, or live activity. The picture re-analyzes itself whenever a `.cs` or `.csproj` file changes.

Nothing leaves your machine: the server listens on localhost only and refuses API calls from other sites.

## Run it

Needs the .NET 10 SDK.

```bash
dotnet run --project src/CodeToLife -- path/to/YourSolution.sln
```

The argument can be a `.sln`, `.slnx`, `.csproj` or a folder (default: the current directory). It opens your browser at `http://localhost:5170`. Options: `--port <n>`, `--no-browser`, and `--ai <openrouter|URL>` with `--ai-model <name>` for explanations (below).

Try it on the bundled sample, a little shop that keeps taking orders:

```bash
dotnet build samples/SampleShop
dotnet run --project src/CodeToLife -- samples/SampleShop
```

Then, in the page:

1. Press **★ Start here** and step through the tour: it starts at the top-level code in `Program.cs` and follows `OrderService` into the pricing rules. Then switch **Map** to **Types**.
2. Double-click **OrderService** to see its methods and what they call.
3. **Live** tab: run `samples/SampleShop/bin/Debug/net10.0/SampleShop.exe` in a terminal, click ⟳, pick the ★ process, **Watch**. The recommendation engine glows as it burns CPU; **Count instances** shows orders piling up.
4. **Debug** tab (needs netcoredbg, below): click OrderService's `CheckoutAsync` → **● Breakpoint**, then **▶ Start** with arguments `--once`. When it stops, the stack lights up from the top-level code through `OrderService`. Press **▶▶ Animate** to watch it step through the pricing rules.
5. **Color by → Test coverage**: pricing is tested, checkout and payments aren't. Click `PricingEngine` → **⚠ Impact** to see what depends on it.
6. **Rules** tab: **✓ Check** finds the deliberate violation (`OrderService` holds the concrete `Inventory`), drawn in red.
7. **Live** tab with the shop running: `FakePaymentGateway.ChargeAsync` shows up under **Waiting**, blue on the map, even though it never uses the CPU.

To install it as a global tool:

```bash
dotnet pack src/CodeToLife -c Release -o nupkg
dotnet tool install -g CodeToLife --add-source ./nupkg
codetolife path/to/YourSolution.sln
```

## Learning a codebase

- **Folders** (the **Map** menu) groups the code into at most 14 folders, merging the deepest into their parents, with an arrow wherever one folder's types use another's. Click a folder for what it's for, what it uses and what uses it, and its files; double-click it (or **Show its types**) to see its classes highlighted on the types map.
- **★ Start here** starts at the program's entry point (top-level statements or `Main` in the app project with the most addresses), then each step follows whatever leads to the most code: what a type creates, calls or holds, and from an interface on to the classes that implement it. Then come the controllers and Razor Pages, which nothing in the code calls because ASP.NET Core finds them by itself, then the types used all over. Each step says why it's there.
- **Pages** finds `app.MapGet("/x", …)` and friends (with `MapGroup` prefixes followed back through locals), controller actions (`[Route]` and `[HttpGet("…")]` templates with `[controller]` and `[action]` filled in, or the default `/{controller}/{action}` route) and Razor Pages (from the file's path and its `@page` template). Pick one to see the request step by step, the calls it makes three levels deep (through interfaces to the classes behind them), and the `.cshtml` it renders, with its source.

### Explanations

The details panel has a **What it does** section for every class and method. There are three ways to fill it in:

- **Claude, through the MCP server.** Register `codetolife-mcp` (below) and ask: *Use explain_next and save_explanation to explain this project for a new developer.*
- **An AI from the page.** Start Code to Life with `--ai openrouter` (set `OPENROUTER_API_KEY`; the model defaults to `openrouter/auto`, or pick one with `--ai-model`) or `--ai <URL>` for any OpenAI-compatible server such as Unsloth Studio, LM Studio, Ollama or llama.cpp. A URL can point at the server's web page (`http://host:8888/chat`): the API is taken to be `/v1` on the same address. If it needs a key, put it in `CODETOLIFE_AI_KEY`. Then click **Explain with AI** on anything.
- **In bulk from the command line:**

  ```bash
  codetolife explain path/to/YourSolution.sln --ai openrouter --limit 25
  ```

  It explains the most connected types first, then methods, skipping tests and anything already explained.

Explanations are saved in `.codetolife/explanations.json`, keyed by type or member id, so they can be committed and shared. Each remembers a hash of the code it describes and is marked as possibly out of date when that code changes. Keys only ever come from environment variables, never the command line or a URL.

## The debugger

Stepping uses **netcoredbg**, Samsung's open-source (MIT) .NET debugger, over the Debug Adapter Protocol, the same protocol VS Code speaks. (Microsoft's `vsdbg` isn't used: its license only allows Microsoft's own tools.) Install it with:

```bash
pwsh tools/Get-Netcoredbg.ps1
```

That puts it in `tools/netcoredbg`, where Code to Life looks first (tested with netcoredbg 3.2.0 on Windows). It also checks the `CODETOLIFE_NETCOREDBG` environment variable and your `PATH`.

The **Live** view doesn't need it: it reads stacks with [ClrMD](https://github.com/microsoft/clrmd) from a snapshot of the process.

Shortcuts: <kbd>F5</kbd> start / continue, <kbd>F10</kbd> step over, <kbd>F11</kbd> step into, <kbd>Shift</kbd>+<kbd>F11</kbd> step out, <kbd>Ctrl</kbd>+<kbd>K</kbd> search, <kbd>Esc</kbd> back out, <kbd>F</kbd> fit.

The **Build** button runs `dotnet build -c Debug` on the chosen project, so the debugger runs the code you're looking at. **Open in editor** links use VS Code (`code --goto`); set `CODETOLIFE_EDITOR` to use something else that understands `--goto file:line`.

## Tests, impact and rules

**Tests** are found by their attributes (xUnit, NUnit, MSTest) and followed through every call, including calls through interfaces into the classes that implement them. That's static: it counts code a test calls even if nothing asserts on it, and it can't see code reached only by reflection. It needs no test run, works on half-written code, and tells you which tests to run: **▶ Run** uses `dotnet test --filter` with exactly those tests.

**Architecture rules** live in `.codetolife/rules.json` (comments allowed):

```jsonc
{
  "layers": { "Domain": "MyApp.Domain.*", "Data": "MyApp.Data.*, project:MyApp.Persistence" },
  "rules": [
    { "from": "Domain", "notTo": "Data", "because": "the domain shouldn't know how it's stored" },
    { "to": "DbContext", "onlyFrom": "Data", "kinds": ["has", "creates"] },
    { "from": "*", "notTo": "MyApp.Legacy.*", "except": "MyApp.Migration.*" }
  ]
}
```

Patterns are layer names, `project:Name`, or globs over namespaces and type names (`MyApp.Data.*` also matches the `MyApp.Data` namespace itself). `kinds` limits a rule to some connections (inherits, implements, has, calls, creates, uses). The **Rules** tab edits the file, suggests layers from your namespaces, and draws violations in red. For CI:

```bash
codetolife check path/to/YourSolution.sln --format github
```

It exits with 1 when a rule is broken (2 when the rules can't be read). `--format github` prints annotations that GitHub Actions shows on the pull request.

## The MCP server

`codetolife-mcp` is a [Model Context Protocol](https://modelcontextprotocol.io) server over stdio, so an AI assistant can ask the graph instead of grepping: who really calls a method (through interfaces), the call path between two methods, what a change would break, which tests to run, whether the architecture rules hold, where a stack trace from a log points, and it can run the program under the debugger, stop at a method and read the locals.

Register it with Claude Code from your project's folder (it analyzes the current directory, or the path you give it):

```bash
claude mcp add codetolife -- dotnet run --project /path/to/code-to-life/src/CodeToLife.Mcp
```

or, installed as a tool:

```bash
dotnet pack src/CodeToLife.Mcp -c Release -o nupkg
dotnet tool install -g CodeToLife.Mcp --add-source ./nupkg
claude mcp add codetolife -- codetolife-mcp
```

Tools: `overview`, `find`, `describe`, `callers`, `callees`, `path_between`, `impact`, `tests_for`, `check_architecture`, `map_stack_trace`, `scan_log`, `open`, and the debugger: `debug_launch` (with breakpoints by method name or `file:line`), `debug_step`, `debug_continue`, `debug_pause`, `debug_evaluate`, `debug_where`, `debug_breakpoints`, `debug_stop`, and for explanations `explain_next` (what still needs one, with its source) and `save_explanation`. Each debugger command waits for the program to stop and answers with the mapped stack, the locals and the code around the current line. The debugger needs netcoredbg; installed as a global tool, point `CODETOLIFE_NETCOREDBG` at it.

## What changed (PR Buddy)

The changes overlay reuses PR Buddy's `GitDiffReader`, `SemanticDiffer` and rules. It's linked in automatically when PR Buddy is cloned next to this repository:

```
Code/
  code-to-life/
  pr-buddy/        ← https://github.com/anefiox/pr-buddy
```

Elsewhere, build with `-p:PrBuddyPath=/path/to/pr-buddy`. Without it everything else still works and the tab explains how to turn it on.

In the **What changed** tab, pick a base branch (default: `origin/main`, `origin/master`, `main` or `master`, compared from the merge base like GitHub) and whether to include uncommitted work. Below the changes are the tests to run for them and the changed code no test reaches.

## How it works

```
src/
  CodeToLife.Analysis   Roslyn: projects → compilations → types, members, edges, calls
  CodeToLife.Runtime    DAP client + debug session (netcoredbg), ClrMD live sampler,
                        EventPipe tracer, and FrameMapper, which maps stack frames back
                        to graph nodes
  CodeToLife.Insights   Built on the graph: call-graph index (through interfaces), test
                        map, impact analysis, architecture rules, stack-trace/log parsing,
                        and Codebase, which keeps it all fresh as files change
  CodeToLife            ASP.NET Core host: JSON API, Server-Sent Events, PR Buddy overlay,
                        plan store, `check` command; wwwroot is the page (Cytoscape.js)
  CodeToLife.Mcp        The MCP server (stdio)
samples/SampleShop      A small app to explore and debug, with a few tests and rules
tests/CodeToLife.Tests  Graph building, frame mapping, the DAP wire format, insights
```

**Analysis doesn't run MSBuild.** Design-time builds are slow, need a matching SDK, and fail on half-written code, which is exactly when you want a picture. Instead each project is compiled straight from its `.cs` files with Roslyn, referenced projects first, against the running .NET runtime plus its NuGet packages (from `obj/project.assets.json` after a restore, otherwise from the global package cache, following dependencies) and anything already in its `bin` folder. Every type declared in source resolves, which is all the graph needs. If packages can't be found, the sidebar says how many compile errors that caused; types still show, and `dotnet restore` fills in the missing calls.

**Mapping a running program back to the source** goes two ways. The debugger reports file and line, mapped to the innermost member whose span contains it. The live sampler reports CLR names like `OrderService+<CheckoutAsync>d__6.MoveNext` or `Program+<>c.<<Main>$>b__0_0`; `FrameMapper` sees through the classes the compiler generates for async methods, iterators, lambdas, closures and local functions, and handles generics, nested types, constructors and property accessors.

**Runtime events** come from EventPipe, the runtime's built-in event stream (what `dotnet-trace` uses): every `await` reports when it starts and finishes waiting, the runtime reports each exception and a sample of allocations, and `HttpClient` reports requests. Each event's stack is resolved with TraceEvent and mapped back like the sampler's.

Complexity is counted the same way as PR Buddy's PRB013 rule: 1 plus one per branch point.

## Limits

- C# only. F# and VB projects are skipped.
- Source generators don't run, so types they'd generate are missing (and `[GeneratedRegex]` methods show as unimplemented, harmlessly).
- Stack sampling sees code that's *running* and misses very short calls; leave it running a while. Waiting async code shows through runtime events instead.
- The test map is static (see above), and impact's "string mentions" are a text search, so expect the odd false positive.
- With netcoredbg 3.2.0 on Windows, the debugged program's own console output didn't come through as debugger output events, so it may be missing from the Output pane and from `debug_*` results.
- Pages only covers ASP.NET Core minimal APIs, controllers and Razor Pages; Blazor `@page` components, gRPC services and conventional routes other than `{controller}/{action}` aren't listed. An address built at run time is shown as written in the code.
- Very large solutions (thousands of types) render, but are easier to read with test projects hidden, namespaces filtered, or **Focus** on one class.

## Development

```bash
dotnet test
```

Front-end libraries (Cytoscape.js, cytoscape-fcose, cytoscape-dagre and their dependencies, all MIT) are vendored in `src/CodeToLife/wwwroot/lib`, so the page works offline.
